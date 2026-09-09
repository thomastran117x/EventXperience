using System;

using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <summary>
    /// Adds the recently-viewed history.
    /// <para>
    /// Two tables rather than one. RecentlyViewedEvents holds the history itself, capped per user
    /// and swept on age. RecentlyViewedSettings holds the opt-out, kept separate from the Users
    /// table so the preference is owned by the feature that means something by it and disappears
    /// with the feature flag rather than lingering as a stray column on the identity aggregate.
    /// </para>
    /// <para>
    /// A row in the settings table exists only once a user has actually touched the toggle; an
    /// absent row means tracking is on.
    /// </para>
    /// </summary>
    public partial class addrecentlyviewedevents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecentlyViewedEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    ViewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecentlyViewedEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecentlyViewedEvents_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecentlyViewedEvents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecentlyViewedSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecentlyViewedSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecentlyViewedSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecentlyViewedEvents_EventId",
                table: "RecentlyViewedEvents",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_RecentlyViewedEvents_UserId_EventId",
                table: "RecentlyViewedEvents",
                columns: new[] { "UserId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecentlyViewedEvents_UserId_ViewedAt",
                table: "RecentlyViewedEvents",
                columns: new[] { "UserId", "ViewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecentlyViewedEvents_ViewedAt",
                table: "RecentlyViewedEvents",
                column: "ViewedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RecentlyViewedSettings_UserId",
                table: "RecentlyViewedSettings",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecentlyViewedEvents");

            migrationBuilder.DropTable(
                name: "RecentlyViewedSettings");
        }
    }
}
