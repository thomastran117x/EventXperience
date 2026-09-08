using backend.main.infrastructure.database.core;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <summary>
    /// Adds the presentation form of the username, so <c>ThomasT</c> survives storage.
    /// </summary>
    /// <remarks>
    /// <c>Users.Username</c> stays the sole lookup key: citext, unique, and the only value hashed
    /// into the bloom filter. <c>UsernameDisplay</c> is plain varchar, unindexed, and constrained to
    /// differ from it by letter case alone.
    ///
    /// The backfill is total — every existing row gets a display equal to its current username — so
    /// no read has to cope with a null on an account that has a username. Rows predating the format
    /// rules (the <c>20260815023000_backfillusernames</c> population) are copied verbatim like any
    /// other; the check below compares against the stored value, not against the format rules, so it
    /// cannot reject them.
    /// </remarks>
    [DbContext(typeof(AppDatabaseContext))]
    [Migration("20260908120000_usernamedisplay")]
    public partial class UsernameDisplay : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UsernameDisplay",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // The cast is load-bearing: Username is citext, and assigning it to a varchar column and
            // comparing it with lower() without one leaves the operator resolution to chance.
            migrationBuilder.Sql(
                """
                UPDATE "Users"
                SET "UsernameDisplay" = "Username"::text
                WHERE "Username" IS NOT NULL;
                """
            );

            // Nothing in the application layer can enforce this across every future write path, and
            // a violation is close to undetectable after the fact: the account would render under
            // one handle while resolving under another. Better a failed write than a row whose
            // displayed name and profile URL quietly disagree.
            migrationBuilder.Sql(
                """
                ALTER TABLE "Users"
                ADD CONSTRAINT "CK_Users_UsernameDisplay_Normalizes"
                CHECK (
                    "UsernameDisplay" IS NULL
                    OR lower("UsernameDisplay") = lower("Username"::text)
                );
                """
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Users"
                DROP CONSTRAINT IF EXISTS "CK_Users_UsernameDisplay_Normalizes";
                """
            );

            migrationBuilder.DropColumn(
                name: "UsernameDisplay",
                table: "Users");
        }
    }
}
