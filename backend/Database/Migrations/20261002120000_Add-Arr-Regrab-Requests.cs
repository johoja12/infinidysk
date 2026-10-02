using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

/// <summary>
/// Persists Sonarr/Radarr regrab requests (file-modal Regrab, failed migration
/// imports, and health-repair replacements) so their state survives restarts.
/// Additive: new table only. Back up /config before upgrading.
/// </summary>
[DbContext(typeof(DavDatabaseContext))]
[Migration("20261002120000_Add-Arr-Regrab-Requests")]
public partial class AddArrRegrabRequests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ArrRegrabRequests",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                ArrHost = table.Column<string>(type: "TEXT", nullable: true),
                ArrFileId = table.Column<int>(type: "INTEGER", nullable: true),
                ArrMediaIds = table.Column<string>(type: "TEXT", nullable: true),
                ArrMediaKind = table.Column<string>(type: "TEXT", nullable: true),
                ArrFileRemoved = table.Column<bool>(type: "INTEGER", nullable: false),
                Blocklisted = table.Column<bool>(type: "INTEGER", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                DavItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                DedupKey = table.Column<string>(type: "TEXT", nullable: false),
                ExpectedLinkTarget = table.Column<string>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true),
                LibraryPath = table.Column<string>(type: "TEXT", nullable: true),
                LinkRemoved = table.Column<bool>(type: "INTEGER", nullable: false),
                MigrationBatchIndex = table.Column<int>(type: "INTEGER", nullable: true),
                MigrationSourceReleaseId = table.Column<string>(type: "TEXT", nullable: true),
                NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                PreviousLinkTarget = table.Column<string>(type: "TEXT", nullable: true),
                Reason = table.Column<string>(type: "TEXT", nullable: true),
                ReleaseName = table.Column<string>(type: "TEXT", nullable: false),
                RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ArrRegrabRequests", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ArrRegrabRequests_DedupKey",
            table: "ArrRegrabRequests",
            column: "DedupKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ArrRegrabRequests_Status",
            table: "ArrRegrabRequests",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_ArrRegrabRequests_DavItemId",
            table: "ArrRegrabRequests",
            column: "DavItemId");

        migrationBuilder.CreateIndex(
            name: "IX_ArrRegrabRequests_LibraryPath",
            table: "ArrRegrabRequests",
            column: "LibraryPath");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ArrRegrabRequests");
    }
}
