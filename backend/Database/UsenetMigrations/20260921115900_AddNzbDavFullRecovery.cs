using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.UsenetMigrations
{
    /// <inheritdoc />
    public partial class AddNzbDavFullRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NzbDavMasters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ManifestDigest = table.Column<string>(type: "TEXT", nullable: false),
                    SourceLinkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RecoverableCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbDavMasters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NzbDavBatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MasterId = table.Column<long>(type: "INTEGER", nullable: false),
                    BatchIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    PackageDigest = table.Column<string>(type: "TEXT", nullable: false),
                    SelectionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<long>(type: "INTEGER", nullable: true),
                    PlanDigest = table.Column<string>(type: "TEXT", nullable: true),
                    AppliedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ValidatedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbDavBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NzbDavBatches_NzbDavMasters_MasterId",
                        column: x => x.MasterId,
                        principalTable: "NzbDavMasters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NzbDavBatches_MasterId_BatchIndex",
                table: "NzbDavBatches",
                columns: new[] { "MasterId", "BatchIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NzbDavBatches_PackageDigest",
                table: "NzbDavBatches",
                column: "PackageDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NzbDavBatches_RunId",
                table: "NzbDavBatches",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_NzbDavBatches_Status",
                table: "NzbDavBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_NzbDavMasters_ManifestDigest",
                table: "NzbDavMasters",
                column: "ManifestDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NzbDavMasters_Status",
                table: "NzbDavMasters",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NzbDavBatches");

            migrationBuilder.DropTable(
                name: "NzbDavMasters");
        }
    }
}
