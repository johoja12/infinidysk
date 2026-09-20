using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.UsenetMigrations
{
    /// <inheritdoc />
    public partial class AddNzbDavCanarySource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanaryLibraryRoot",
                table: "SessionState",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePackageRoot",
                table: "SessionState",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "SessionState",
                type: "TEXT",
                nullable: false,
                defaultValue: "altmount");

            migrationBuilder.AddColumn<string>(
                name: "ArticleIdentityDigest",
                table: "ReleaseFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArticleIdentityKind",
                table: "ReleaseFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceFileId",
                table: "ReleaseFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CanaryLibraryRoot",
                table: "MigrationPreferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePackageRoot",
                table: "MigrationPreferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "MigrationPreferences",
                type: "TEXT",
                nullable: false,
                defaultValue: "altmount");

            migrationBuilder.AddColumn<string>(
                name: "ArticleIdentityDigest",
                table: "MigratedFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArticleIdentityKind",
                table: "MigratedFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceFileId",
                table: "MigratedFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CanaryLinks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryRelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalLegacyTarget = table.Column<string>(type: "TEXT", nullable: false),
                    NewRelativeTarget = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationStatus = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationEvidence = table.Column<string>(type: "TEXT", nullable: false),
                    SourcePackageDigest = table.Column<string>(type: "TEXT", nullable: false),
                    ApplyStatus = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanaryLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CanaryLinks_MigrationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "MigrationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseFiles_StoreRef_SourceFileId",
                table: "ReleaseFiles",
                columns: new[] { "StoreRef", "SourceFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_MigratedFiles_MigratedReleaseId_SourceFileId",
                table: "MigratedFiles",
                columns: new[] { "MigratedReleaseId", "SourceFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_CanaryLinks_ApplyStatus",
                table: "CanaryLinks",
                column: "ApplyStatus");

            migrationBuilder.CreateIndex(
                name: "IX_CanaryLinks_CorrelationStatus",
                table: "CanaryLinks",
                column: "CorrelationStatus");

            migrationBuilder.CreateIndex(
                name: "IX_CanaryLinks_RunId_LibraryRelativePath",
                table: "CanaryLinks",
                columns: new[] { "RunId", "LibraryRelativePath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CanaryLinks");

            migrationBuilder.DropIndex(
                name: "IX_ReleaseFiles_StoreRef_SourceFileId",
                table: "ReleaseFiles");

            migrationBuilder.DropIndex(
                name: "IX_MigratedFiles_MigratedReleaseId_SourceFileId",
                table: "MigratedFiles");

            migrationBuilder.DropColumn(
                name: "CanaryLibraryRoot",
                table: "SessionState");

            migrationBuilder.DropColumn(
                name: "SourcePackageRoot",
                table: "SessionState");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "SessionState");

            migrationBuilder.DropColumn(
                name: "ArticleIdentityDigest",
                table: "ReleaseFiles");

            migrationBuilder.DropColumn(
                name: "ArticleIdentityKind",
                table: "ReleaseFiles");

            migrationBuilder.DropColumn(
                name: "SourceFileId",
                table: "ReleaseFiles");

            migrationBuilder.DropColumn(
                name: "CanaryLibraryRoot",
                table: "MigrationPreferences");

            migrationBuilder.DropColumn(
                name: "SourcePackageRoot",
                table: "MigrationPreferences");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "MigrationPreferences");

            migrationBuilder.DropColumn(
                name: "ArticleIdentityDigest",
                table: "MigratedFiles");

            migrationBuilder.DropColumn(
                name: "ArticleIdentityKind",
                table: "MigratedFiles");

            migrationBuilder.DropColumn(
                name: "SourceFileId",
                table: "MigratedFiles");
        }
    }
}
