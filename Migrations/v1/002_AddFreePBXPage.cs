using Rock.Plugin;

namespace com.blueboxmoon.FreePBX.Migrations.v1
{
    [MigrationNumber( 2, "1.11.0" )]
    public class AddFreePBXPage : Migration
    {
        public override void Up()
        {
            //
            // Add a new FreePBX page to the Installed Plugins page.
            //
            RockMigrationHelper.AddPage( true,
                "5B6DBC42-8B03-4D15-8D92-AAFA28FD8616", // Installed Plugins Page
                "D65F783D-87A9-4CC9-8110-E83466A0EADB", // Full-width Layout
                "FreePBX",
                "",
                "13AEC012-52DE-409F-BCF6-64FE0881F125",
                "fa fa-phone-square" );

            //
            // Add an HTML block to the FreePBX page.
            //
            RockMigrationHelper.AddBlock( true,
                "13AEC012-52DE-409F-BCF6-64FE0881F125",
                "",
                "19B61D65-37E3-459F-A44F-DEF0089118A3", // HTML Content Block
                "Content",
                "Main",
                "",
                "",
                0,
                "94E78D07-5B37-4A2A-93CC-7D74101BA5A6" );

            //
            // Set the content of the HTML block.
            //
            RockMigrationHelper.UpdateHtmlContentBlock( "94E78D07-5B37-4A2A-93CC-7D74101BA5A6",
                @"<h1>FreePBX Module</h1>

<p>
    {% assign root = 'Global' | Attribute:'InternalApplicationRoot' %}
    {% assign lastChar = root | Right:1 %}
    {% capture url %}{{ root }}{% if lastChar != '/' %}/{% endif %}Plugins/com_blueboxmoon/FreePBX/Assets/rockrmsinterface.zip{% endcapture %}
    Before you can use the FreePBX plugin you need to install a module on your FreePBX server. You
    can find the module here: <a href='{{ url }}'>{{ url }}</a>
</p>
",
                "679E0EDD-F736-49FD-A816-860762B22131" );
        }

        public override void Down()
        {
            RockMigrationHelper.DeleteBlock( "94E78D07-5B37-4A2A-93CC-7D74101BA5A6" );
            RockMigrationHelper.DeletePage( "13AEC012-52DE-409F-BCF6-64FE0881F125" );
        }
    }
}
