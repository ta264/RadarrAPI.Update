﻿using Microsoft.EntityFrameworkCore.Migrations;

namespace RadarrAPI.Database.Migrations
{
    public partial class SupportAnyBranch : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Branch",
                table: "Updates",
                nullable: true,
                oldClrType: typeof(sbyte),
                oldType: "tinyint");

            migrationBuilder.Sql("DELETE FROM Updates WHERE Branch = '0'");

            for (int i = 1; i <= 4; i++)
            {
                var branchName = ((OldBranchEnum)i).ToString().ToLower();
                migrationBuilder.Sql($"UPDATE Updates SET Branch = '{branchName}' WHERE Branch = '{i}'");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            for (int i = 1; i <= 4; i++)
            {
                var branchName = ((OldBranchEnum)i).ToString().ToLower();
                migrationBuilder.Sql($"UPDATE Updates SET Branch = '{i}' WHERE Branch = '{branchName}'");
            }

            migrationBuilder.AlterColumn<sbyte>(
                name: "Branch",
                table: "Updates",
                type: "tinyint",
                nullable: false,
                oldClrType: typeof(string),
                oldNullable: true);
        }

        private enum OldBranchEnum
        {
            Unknown = 0,
            Develop = 1,
            Nightly = 2,
            Master = 3,
            Aphrodite = 4
        }
    }
}
