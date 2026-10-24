using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexusForever.Database.Character.Migrations
{
    /// <summary>
    /// Tradeskill state per character: learned skills and learned/crafted schematics. Written by hand (no EF tooling in this setup);
    /// earlier JSON state in /cache/tradeskills is imported on first load by the world server.
    /// </summary>
    [DbContext(typeof(CharacterContext))]
    [Migration("20261008120000_CharacterTradeskills")]
    public partial class CharacterTradeskills : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "character_tradeskill",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint(20) unsigned", nullable: false, defaultValue: 0ul),
                    tradeskillId = table.Column<uint>(type: "int(10) unsigned", nullable: false, defaultValue: 0u),
                    xp = table.Column<uint>(type: "int(10) unsigned", nullable: false, defaultValue: 0u),
                    active = table.Column<byte>(type: "tinyint(3) unsigned", nullable: false, defaultValue: (byte)1),
                    talentPoints = table.Column<uint>(type: "int(10) unsigned", nullable: false, defaultValue: 0u),
                    talents = table.Column<string>(type: "varchar(200)", nullable: true, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.id, x.tradeskillId });
                    table.ForeignKey(
                        name: "FK__character_tradeskill_id__character_id",
                        column: x => x.id,
                        principalTable: "character",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "character_tradeskill_schematic",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint(20) unsigned", nullable: false, defaultValue: 0ul),
                    schematicId = table.Column<uint>(type: "int(10) unsigned", nullable: false, defaultValue: 0u),
                    discovered = table.Column<byte>(type: "tinyint(3) unsigned", nullable: false, defaultValue: (byte)0),
                    crafted = table.Column<byte>(type: "tinyint(3) unsigned", nullable: false, defaultValue: (byte)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.id, x.schematicId });
                    table.ForeignKey(
                        name: "FK__character_tradeskill_schematic_id__character_id",
                        column: x => x.id,
                        principalTable: "character",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "character_tradeskill_schematic");
            migrationBuilder.DropTable(name: "character_tradeskill");
        }
    }
}
