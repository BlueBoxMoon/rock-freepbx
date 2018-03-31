using Rock.Plugin;

namespace com.blueboxmoon.FreePBX.Migrations.v1
{
    [MigrationNumber(1, "1.7.3")]
    public class AddInteractionComponent : Migration
    {
        public override void Up()
        {
            Sql( string.Format( @"DECLARE @ChannelId INT = (SELECT TOP 1 [Id] FROM [InteractionChannel] WHERE [Guid] = 'B3904B57-62A2-57AC-43EA-94D4DEBA3D51')
INSERT INTO [InteractionComponent] ([Name], [ChannelId], [Guid])
VALUES ('FreePBX', @ChannelId, '{0}')
", SystemGuid.InteractionComponent.FREEPBX ) );
        }

        public override void Down()
        {
            Sql( string.Format( @"DELETE FROM [InteractionComponent] WHERE [Guid] = '{0}'", SystemGuid.InteractionComponent.FREEPBX ) );
        }
    }
}
