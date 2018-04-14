using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net;

using RestSharp;

using Rock;
using Rock.Attribute;
using Rock.Data;
using Rock.Model;
using Rock.Pbx;
using Rock.Web.Cache;

using com.blueboxmoon.FreePBX.RestClasses;

// Implement minimum duration filter.
namespace com.blueboxmoon.FreePBX.Provider
{
    /// <summary>
    /// FreePBX provider for Rock.
    /// </summary>
    [Description( "FreePBX PBX Provider." )]
    [Export( typeof( PbxComponent ) )]
    [ExportMetadata( "ComponentName", "FreePBX" )]

    [TextField( "Server URL", "The URL of the PBX node (http://myserver:80)", true, order: 0 )]
    [TextField( "Username", "The username to connect with.", true, order: 1 )]
    [TextField( "Password", "The password to connect with.", true, isPassword: true, order: 2 )]
    [IntegerField( "Minimum Duration", "The minimum duration in seconds a call must be in order to be imported.", true, 10, order: 3 )]
    [BooleanField( "Skip Internal Calls", "If set to Yes then a call that is determined to be an internal call (extension to extension) will not be imported.", false, order: 4 )]
    [CodeEditorField( "Phone Extension Template", "Lava template to use to get the extension from the internal phone. This helps translate the full phone number to just the internal extension (e.g. (602) 555-2345 to 2345). The phone number will be passed into the template as the variable 'PhoneNumber'.", Rock.Web.UI.Controls.CodeEditorMode.Lava, Rock.Web.UI.Controls.CodeEditorTheme.Rock, 200, false, "{{ PhoneNumber | Right:4 }}", order: 5 )]
    [CodeEditorField( "Phone Number Template", "Lava template to use to get the phone number from a call record. Use this to make sure a full phone number is in the format that Rock expects (e.g. 10 digits). The phone number will be passed into the template as the variable 'PhoneNumber'.", Rock.Web.UI.Controls.CodeEditorMode.Lava, Rock.Web.UI.Controls.CodeEditorTheme.Rock, 200, false, "{{ PhoneNumber | Right:10 }}", order: 6 )]
    [CodeEditorField( "Origination Rules Template", "Lava template that will be applied to both the source and destination number when originating a call. Lava variables include {{ PhoneNumber }}.", Rock.Web.UI.Controls.CodeEditorMode.Lava, Rock.Web.UI.Controls.CodeEditorTheme.Rock, 200, false, "{{ PhoneNumber }}", order: 7 )]
    [CodeEditorField( "Caller Id Template", "Lava template that will be used to generate the caller Id.", Rock.Web.UI.Controls.CodeEditorMode.Lava, Rock.Web.UI.Controls.CodeEditorTheme.Rock, 200, true, "{{ CallerId }}", order: 8 )]
    [TextField( "Origination URL", "Enter a URL here to use a custom origination URL. If blank then the default origination system will be used. <span class='tip tip-lava'></span>", false, "", order: 9 )]
    public partial class FreePBX : PbxComponent
    {
        #region Base Method Overrides

        /// <summary>
        /// Gets a value indicating whether this provider supports call origination.
        /// </summary>
        public override bool SupportsOrigination
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// Originates the a call from a person.
        /// </summary>
        /// <param name="fromPerson">The person who has initiated the call request.</param>
        /// <param name="toPhone">The phone number to be called.</param>
        /// <param name="callerId">The caller identifier.</param>
        /// <returns>True if the call was originated.</returns>
        public override bool Originate( Person fromPerson, string toPhone, string callerId, out string message )
        {
            var phoneTypeId = PersonService.GetUserPreference( fromPerson, Rock.SystemKey.UserPreference.ORIGINATE_CALL_SOURCE ).AsIntegerOrNull();

            phoneTypeId = phoneTypeId ?? this.GetAttributeValue( "InternalPhoneType" ).AsIntegerOrNull();

            if ( !phoneTypeId.HasValue )
            {
                message = "Could not determine the phone to use to originate this call.";

                return false;
            }

            var phoneNumber = new PhoneNumberService( new RockContext() ).Queryable()
                .Where( p => p.PersonId == fromPerson.Id && p.NumberTypeValueId == phoneTypeId.Value )
                .FirstOrDefault();

            if ( phoneNumber == null )
            {
                var phoneType = DefinedValueCache.Read( phoneTypeId.Value );

                message = string.Format( "There is no {0} phone number configured.", phoneType.Value.ToLower() );

                return false;
            }

            return Originate( phoneNumber.Number, toPhone, callerId, out message );
        }

        /// <summary>
        /// Originates a call from a phone number.
        /// </summary>
        /// <param name="fromPhone">The phone number of the person initiating the call.</param>
        /// <param name="toPhone">The phone number to be called.</param>
        /// <param name="callerId">The caller identifier.</param>
        /// <returns>True if the call was originated.</returns>
        public override bool Originate( string fromPhone, string toPhone, string callerId, out string message )
        {
            //
            // Run the numbers through the calling rules.
            //
            var ruleTemplate = GetAttributeValue( "OriginationRulesTemplate" );
            var mergeFields = new Dictionary<string, object>();

            mergeFields.Add( "PhoneNumber", fromPhone );
            fromPhone = ruleTemplate.ResolveMergeFields( mergeFields ).Trim();

            mergeFields.AddOrReplace( "PhoneNumber", toPhone );
            toPhone = ruleTemplate.ResolveMergeFields( mergeFields ).Trim();

            return OriginateFreePBX( fromPhone, toPhone, callerId, out message );
        }

        /// <summary>
        /// Downloads the CDR records from the phone system.
        /// </summary>
        /// <param name="downloadSuccessful">On output contains true if the download was successful.</param>
        /// <param name="startDate">The start time </param>
        /// <returns></returns>
        public override string DownloadCdr( out bool downloadSuccessful, DateTime? startDateTime = null )
        {
            bool skipInternalCalls = GetAttributeValue( "SkipInternalCalls" ).AsBoolean();
            int minimumDuration = GetAttributeValue( "MinimumDuration" ).AsIntegerOrNull() ?? 10;

            //
            // If we don't have a start date (shouldn't ever happen), pick a safe date.
            //
            if ( !startDateTime.HasValue )
            {
                startDateTime = new DateTime( 2000, 1, 1 );
            }

            try
            {
                var personAliasPhoneCache = new Dictionary<string, int?>();

                //
                // Get our interaction type and the person alias type id.
                //
                var interactionComponentId = InteractionComponentCache.Read( SystemGuid.InteractionComponent.FREEPBX ).Id;
                var personAliasTypeId = EntityTypeCache.Read( "Rock.Model.PersonAlias" ).Id;

                //
                // Get list of the current interaction foreign keys for the same
                // timeframe to ensure we don't get duplicates.
                //
                List<string> currentInteractions;
                using ( var rockContext = new RockContext() )
                {
                    var interactionStartDate = startDateTime.Value.Date;
                    currentInteractions = new InteractionService( rockContext ).Queryable()
                        .Where( i => i.InteractionComponentId == interactionComponentId && i.InteractionDateTime >= interactionStartDate )
                        .Select( i => i.ForeignKey ).ToList();
                }

                //
                // Get our internal extensions.
                //
                var extensionList = GetInternalExtensions();

                //
                // Loop through and get call records until we run out of data.
                //
                int importedRecords = 0;
                for ( int pageNumber = 1; ; pageNumber++ )
                {
                    var records = GetCdrRecords( pageNumber, startDateTime.Value );
                    if ( records.Count == 0 )
                    {
                        break;
                    }

                    //
                    // Loop through each record and process it.
                    //
                    foreach ( var record in records )
                    {
                        //
                        // Skip any records that we already know of or are not long enough.
                        //
                        if ( currentInteractions.Contains( record.RecordKey ) || record.Duration < minimumDuration )
                        {
                            continue;
                        }

                        using ( var rockContext = new RockContext() )
                        {
                            var interactionService = new InteractionService( rockContext );
                            var personService = new PersonService( rockContext );
                            var interaction = new Interaction();

                            //
                            // Try to find the source and destination persons. First try the extension
                            // list and then search the database for a matching phone number.
                            //
                            bool sourceInternal;
                            bool destinationInternal;
                            var sourcePersonAliasId = GetPersonAliasIdForPhone( record.Source, personService, extensionList, personAliasPhoneCache, out sourceInternal );
                            var destinationPersonAliasId = GetPersonAliasIdForPhone( record.Destination, personService, extensionList, personAliasPhoneCache, out destinationInternal );

                            //
                            // Check if we are skipping internal calls.
                            //
                            bool skipSource = sourceInternal || !sourcePersonAliasId.HasValue;
                            bool skipDestination = destinationInternal || !destinationPersonAliasId.HasValue;
                            if ( skipInternalCalls && skipSource && skipDestination )
                            {
                                continue;
                            }

                            //
                            // Try to ensure the Person is always the non-staff member.
                            //
                            if ( record.Direction == CdrDirection.Incoming )
                            {
                                interaction.PersonAliasId = sourcePersonAliasId;
                                interaction.EntityId = destinationPersonAliasId;
                            }
                            else
                            {
                                interaction.PersonAliasId = destinationPersonAliasId;
                                interaction.EntityId = sourcePersonAliasId;
                            }

                            //
                            // Only save it if we actually found a person.
                            //
                            if ( interaction.PersonAliasId.HasValue )
                            {
                                interaction.Operation = record.Direction.ToString();
                                interaction.InteractionData = record.ToJson();
                                interaction.InteractionComponentId = interactionComponentId;
                                interaction.RelatedEntityTypeId = personAliasTypeId;
                                interaction.ForeignKey = record.RecordKey;
                                interaction.InteractionDateTime = record.StartDateTime.Value;

                                interactionService.Add( interaction );
                                rockContext.SaveChanges();

                                importedRecords++;
                            }
                        }
                    }
                }

                downloadSuccessful = true;
                return string.Format( "FreePBX: Imported {0} records.", importedRecords );
            }
            catch ( Exception ex )
            {
                ExceptionLogService.LogException( ex );

                downloadSuccessful = false;
                return string.Format( "FreePBX: Experienced an error: {0}.", ex.Message );
            }
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Get a page worth of CdrRecords from the PBX server. This method should
        /// be called repeatedly while incrementing pageNumber until it returns an
        /// empty collection.
        /// </summary>
        /// <param name="pageNumber">The page number of the results to retrieve.</param>
        /// <param name="startDateTime">The starting time to get call records for.</param>
        /// <returns>A list of CdrRecord objects or an empty array if all records are retrieved.</returns>
        private List<CdrRecord> GetCdrRecords( int pageNumber, DateTime startDateTime )
        {
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            var client = new RestClient( GetAttributeValue( "ServerURL" ) );

            //
            // Prepare the phone number template.
            //
            var phoneNumberTemplate = GetAttributeValue( "PhoneNumberTemplate" );
            var phoneNumberMergeFields = new Dictionary<string, object>();

            var request = new RestRequest( "admin/api/rockrmsinterface/getCelData" );
            request.AddParameter( "username", GetAttributeValue( "Username" ) );
            request.AddParameter( "password", GetAttributeValue( "Password" ) );
            request.AddParameter( "date", startDateTime.ToString( "yyyy-MM-dd HH:mm:ss" ) );
            request.AddParameter( "page", pageNumber );
            request.AddParameter( "limit", 1000 );

            var response = client.Execute<List<CallRecord>>( request );

            if ( response.StatusCode != System.Net.HttpStatusCode.OK )
            {
                throw new Exception( response.Content.Left( 2000 ), response.ErrorException );
            }

            var records = new List<CdrRecord>();
            foreach ( var call in response.Data )
            {
                var record = new CdrRecord();

                switch ( call.direction )
                {
                    case "incoming":
                        record.Direction = CdrDirection.Incoming;
                        break;

                    case "outgoing":
                        record.Direction = CdrDirection.Outgoing;
                        break;

                    default:
                        record.Direction = CdrDirection.Unknown;
                        break;
                }

                //
                // Translate the source and destination phone numbers.
                //
                phoneNumberMergeFields.AddOrReplace( "PhoneNumber", call.src.AsNumeric() );
                record.Source = phoneNumberTemplate.ResolveMergeFields( phoneNumberMergeFields, null ).Trim();
                phoneNumberMergeFields.AddOrReplace( "PhoneNumber", call.dst.AsNumeric() );
                record.Destination = phoneNumberTemplate.ResolveMergeFields( phoneNumberMergeFields, null ).Trim();

                record.CallerId = call.src_name;
                record.StartDateTime = call.starttime.AsDateTime();
                record.EndDateTime = call.endtime.AsDateTime();
                record.Duration = call.duration;
                record.RecordKey = call.id;

                records.Add( record );
            }

            return records;
        }

        /// <summary>
        /// Originates the call between two phone numbers.
        /// </summary>
        /// <param name="fromPhone">The phone number of the person initiating the call.</param>
        /// <param name="toPhone">The phone number to be called.</param>
        /// <param name="callerId">The caller identifier.</param>
        /// <returns>True if the call was originated.</returns>
        public bool OriginateFreePBX( string fromPhone, string toPhone, string callerId, out string message )
        {
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            message = string.Empty;

            //
            // Get the final Caller Id to use.
            //
            var callerIdTemplate = GetAttributeValue( "CallerIdTemplate" );
            var originationUrlTemplate = GetAttributeValue( "OriginationURL" );
            var mergeFields = new Dictionary<string, object>
            {
                { "CallerId", callerId },
                { "FromPhoneNumber", fromPhone },
                { "ToPhoneNumber", toPhone }
            };
            callerId = callerIdTemplate.ResolveMergeFields( mergeFields, null ).Trim();

            //
            // Check if they want to use a custom origination URL.
            //
            if ( !string.IsNullOrWhiteSpace( originationUrlTemplate ) )
            {
                var uri = new Uri( originationUrlTemplate.ResolveMergeFields( mergeFields, null ).Trim() );

                var client = new RestClient( string.Format( "{0}://{1}", uri.Scheme, uri.Authority ) );
                var request = new RestRequest( uri.PathAndQuery );
                var response = client.Execute( request );

                if ( response.StatusCode != HttpStatusCode.OK )
                {
                    message = "Error contacting Origination URL.";
                    return false;
                }

                return true;
            }
            else
            {
                var client = new RestClient( GetAttributeValue( "ServerURL" ) );

                var request = new RestRequest( "admin/api/rockrmsinterface/originate" );
                request.AddParameter( "username", GetAttributeValue( "Username" ) );
                request.AddParameter( "password", GetAttributeValue( "Password" ) );
                request.AddParameter( "from", fromPhone );
                request.AddParameter( "to", toPhone );
                request.AddParameter( "callerid", callerId );

                var response = client.Execute<OriginateStatus>( request );

                if ( response.StatusCode != HttpStatusCode.OK || response.Data == null || response.Data.message == null )
                {
                    message = "Error contacting PBX server.";
                    return false;
                }

                if ( response.Data.status != "Success" )
                {
                    message = response.Data.message;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Gets the internal extensions.
        /// </summary>
        /// <param name="rockContext">The rock context to operate in.</param>
        /// <returns>A list of internal extension information.</returns>
        private List<ExtensionMap> GetInternalExtensions( RockContext rockContext = null )
        {
            rockContext = rockContext ?? new RockContext();

            var internalPhoneTypeId = GetAttributeValue( "InternalPhoneType" ).AsInteger();

            //
            // Get list of the internal extensions and who is tied to them.
            //
            var extensionList = new PhoneNumberService( rockContext ).Queryable()
                .Where( p => p.NumberTypeValueId == internalPhoneTypeId )
                .Select( p => new ExtensionMap
                {
                    Number = p.Number,
                    Extension = p.Number,
                    PersonAlias = p.Person.Aliases.FirstOrDefault()
                } )
                .ToList();

            //
            // Run lava template over the extension to translate the full number into an extension.
            //
            var translationTemplate = this.GetAttributeValue( "PhoneExtensionTemplate" );
            foreach ( var extension in extensionList )
            {
                var mergeFields = new Dictionary<string, object>();
                mergeFields.Add( "PhoneNumber", extension.Number );
                extension.Extension = translationTemplate.ResolveMergeFields( mergeFields );
            }

            return extensionList;
        }

        /// <summary>
        /// Get the Person Alias Id for the given phone number.
        /// </summary>
        /// <param name="phoneNumber">The phone number to be found.</param>
        /// <param name="personService">The person service to search with.</param>
        /// <param name="extensionList">The internal extension list to check first.</param>
        /// <param name="personAliasPhoneCache">The cache to use for speeding things up.</param>
        /// <param name="internalExtension">On return, set to true if the matched person is from the extensionList.</param>
        /// <returns>A PersonAliasId that identifies the matched person, or null if no match found.</returns>
        private int? GetPersonAliasIdForPhone( string phoneNumber, PersonService personService, List<ExtensionMap> extensionList, Dictionary<string, int?> personAliasPhoneCache, out bool internalExtension )
        {
            internalExtension = false;

            if ( phoneNumber.Length > 0 )
            {
                var personAliasId = extensionList
                    .Where( e => e.Number == phoneNumber || e.Extension == phoneNumber )
                    .Select( e => e.PersonAlias.Id )
                    .Cast<int?>()
                    .FirstOrDefault();

                internalExtension = personAliasId.HasValue;

                if ( !personAliasId.HasValue && personAliasPhoneCache.ContainsKey( phoneNumber ) )
                {
                    return personAliasPhoneCache[phoneNumber];
                }

                if ( !personAliasId.HasValue && phoneNumber.Length > 4 )
                {
                    personAliasId = personService
                        .GetByPhonePartial( phoneNumber )
                        .FirstOrDefault()?.PrimaryAliasId;
                }

                personAliasPhoneCache.AddOrReplace( phoneNumber, personAliasId );

                return personAliasId;
            }

            return null;
        }

        #endregion

        #region Support Classes

        /// <summary>
        /// Class to use for extension mapping
        /// </summary>
        private class ExtensionMap
        {
            /// <summary>
            /// Gets or sets the number.
            /// </summary>
            public string Number { get; set; }

            /// <summary>
            /// Gets or sets the extension.
            /// </summary>
            public string Extension { get; set; }

            /// <summary>
            /// Gets or sets the person alias identifier.
            /// </summary>
            public PersonAlias PersonAlias { get; set; }
        }

        #endregion
    }
}
