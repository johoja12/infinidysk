using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.PostgresMigrations;

/// <summary>
/// Persists Sonarr/Radarr regrab requests (file-modal Regrab, failed migration
/// imports, and health-repair replacements) so their state survives restarts.
/// Additive: new table only. Back up /config before upgrading.
/// </summary>
[DbContext(typeof(PostgresDavDatabaseContext))]
[Migration("20261002120000_Add-Arr-Regrab-Requests")]
public partial class AddArrRegrabRequests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ArrRegrabRequests",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                ArrHost = table.Column<string>(type: "text", nullable: true),
                ArrFileId = table.Column<int>(type: "integer", nullable: true),
                ArrMediaIds = table.Column<string>(type: "text", nullable: true),
                ArrMediaKind = table.Column<string>(type: "text", nullable: true),
                ArrFileRemoved = table.Column<bool>(type: "boolean", nullable: false),
                Blocklisted = table.Column<bool>(type: "boolean", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                DavItemId = table.Column<Guid>(type: "uuid", nullable: true),
                DedupKey = table.Column<string>(type: "text", nullable: false),
                ExpectedLinkTarget = table.Column<string>(type: "text", nullable: true),
                LastError = table.Column<string>(type: "text", nullable: true),
                LibraryPath = table.Column<string>(type: "text", nullable: true),
                LinkRemoved = table.Column<bool>(type: "boolean", nullable: false),
                MigrationBatchIndex = table.Column<int>(type: "integer", nullable: true),
                MigrationSourceReleaseId = table.Column<string>(type: "text", nullable: true),
                NextAttemptAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                PreviousLinkTarget = table.Column<string>(type: "text", nullable: true),
                Reason = table.Column<string>(type: "text", nullable: true),
                ReleaseName = table.Column<string>(type: "text", nullable: false),
                RequestedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                Source = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
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
