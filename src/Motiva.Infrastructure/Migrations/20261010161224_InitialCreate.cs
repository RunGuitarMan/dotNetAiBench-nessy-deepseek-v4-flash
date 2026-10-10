using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Motiva.Domain;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Motiva.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "achievement_grant",
            columns: table => new
            {
                EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                AchievementId = table.Column<long>(type: "bigint", nullable: false),
                Season = table.Column<int>(type: "integer", nullable: false),
                GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_achievement_grant", x => new { x.EmployeeId, x.AchievementId, x.Season });
            });

        migrationBuilder.CreateTable(
            name: "Achievements",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Description = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Achievements", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AuditEntries",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                Actor = table.Column<string>(type: "text", nullable: false),
                Target = table.Column<string>(type: "text", nullable: false),
                Action = table.Column<int>(type: "integer", nullable: false),
                Details = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEntries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "budget",
            columns: table => new
            {
                CampaignId = table.Column<long>(type: "bigint", nullable: false),
                ResourceId = table.Column<long>(type: "bigint", nullable: false),
                Remaining = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_budget", x => new { x.CampaignId, x.ResourceId });
                table.CheckConstraint("CK_budget_nonneg", "\"Remaining\" BETWEEN 0 AND 1000000000000000");
            });

        migrationBuilder.CreateTable(
            name: "Campaigns",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Description = table.Column<string>(type: "text", nullable: true),
                Season = table.Column<int>(type: "integer", nullable: false),
                Start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                End = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                OwnerEmployeeId = table.Column<long>(type: "bigint", nullable: true),
                Audience = table.Column<Audience>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Campaigns", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "challenge_score",
            columns: table => new
            {
                ChallengeId = table.Column<long>(type: "bigint", nullable: false),
                EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                Score = table.Column<long>(type: "bigint", nullable: false),
                FinalizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_challenge_score", x => new { x.ChallengeId, x.EmployeeId });
                table.CheckConstraint("CK_challenge_score_nonneg", "\"Score\" BETWEEN 0 AND 1000000000000000");
            });

        migrationBuilder.CreateTable(
            name: "Challenges",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                StreamId = table.Column<long>(type: "bigint", nullable: false),
                Start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                End = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                State = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Challenges", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Companies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                TimeZoneId = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Companies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "completion",
            columns: table => new
            {
                EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                TaskId = table.Column<long>(type: "bigint", nullable: false),
                PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_completion", x => new { x.EmployeeId, x.TaskId, x.PeriodStart });
            });

        migrationBuilder.CreateTable(
            name: "Employees",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                MasterId = table.Column<int>(type: "integer", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                Tags = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Employees", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "IntegrationGrants",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                IntegrationId = table.Column<long>(type: "bigint", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                CampaignId = table.Column<long>(type: "bigint", nullable: true),
                ResourceId = table.Column<long>(type: "bigint", nullable: true),
                PurchaseSystemId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IntegrationGrants", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Integrations",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                ServiceSub = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Integrations", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Milestones",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                StreamId = table.Column<long>(type: "bigint", nullable: false),
                Threshold = table.Column<int>(type: "integer", nullable: false),
                AchievementId = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Milestones", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OperationRequests",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                InitiatorKey = table.Column<string>(type: "text", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                SourceNo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                CanonicalPayload = table.Column<string>(type: "text", nullable: false),
                Result = table.Column<int>(type: "integer", nullable: false),
                HttpStatus = table.Column<int>(type: "integer", nullable: false),
                ResponseView = table.Column<string>(type: "text", nullable: false),
                OperationId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OperationRequests", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Operations",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                InitiatorKey = table.Column<string>(type: "text", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                SourceNo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Basis = table.Column<string>(type: "text", nullable: true),
                EmployeeId = table.Column<long>(type: "bigint", nullable: true),
                Result = table.Column<int>(type: "integer", nullable: false),
                RejectReason = table.Column<string>(type: "text", nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CampaignId = table.Column<long>(type: "bigint", nullable: true),
                PurchaseSystemId = table.Column<long>(type: "bigint", nullable: true),
                OriginalOperationId = table.Column<long>(type: "bigint", nullable: true),
                CancellationState = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Operations", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "progress",
            columns: table => new
            {
                EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                TaskId = table.Column<long>(type: "bigint", nullable: false),
                PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Value = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_progress", x => new { x.EmployeeId, x.TaskId, x.PeriodStart });
            });

        migrationBuilder.CreateTable(
            name: "ProgressEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceId = table.Column<long>(type: "bigint", nullable: false),
                SourceEventNo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                TaskId = table.Column<long>(type: "bigint", nullable: false),
                SentIncrease = table.Column<int>(type: "integer", nullable: false),
                CreditedIncrease = table.Column<int>(type: "integer", nullable: false),
                AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Completed = table.Column<bool>(type: "boolean", nullable: false),
                RewardOperationId = table.Column<long>(type: "bigint", nullable: true),
                RewardResult = table.Column<string>(type: "text", nullable: true),
                RewardReason = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProgressEvents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseSystems",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                ResourceIds = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseSystems", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Resources",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Resources", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "StatementAttempts",
            columns: table => new
            {
                StatementId = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OwnerWorkerId = table.Column<string>(type: "text", nullable: false),
                LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StatementAttempts", x => x.StatementId);
            });

        migrationBuilder.CreateTable(
            name: "StatementDatas",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                StatementId = table.Column<long>(type: "bigint", nullable: false),
                SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                MovementIdsJson = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StatementDatas", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "StatementDownloads",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                StatementId = table.Column<long>(type: "bigint", nullable: false),
                Url = table.Column<string>(type: "text", nullable: false),
                IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                TransportKey = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StatementDownloads", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Statements",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerActorId = table.Column<long>(type: "bigint", nullable: false),
                OwnerActorType = table.Column<string>(type: "text", nullable: false),
                Scope = table.Column<int>(type: "integer", nullable: false),
                EmployeeId = table.Column<long>(type: "bigint", nullable: true),
                From = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                To = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ResourceId = table.Column<long>(type: "bigint", nullable: true),
                State = table.Column<int>(type: "integer", nullable: false),
                FileRef = table.Column<string>(type: "text", nullable: true),
                ErrorMessage = table.Column<string>(type: "text", nullable: true),
                TransportKey = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Statements", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "stream",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CampaignId = table.Column<long>(type: "bigint", nullable: false),
                Code = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_stream", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "stream_score",
            columns: table => new
            {
                EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                StreamId = table.Column<long>(type: "bigint", nullable: false),
                Season = table.Column<int>(type: "integer", nullable: false),
                Points = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_stream_score", x => new { x.EmployeeId, x.StreamId, x.Season });
                table.CheckConstraint("CK_stream_score_nonneg", "\"Points\" BETWEEN 0 AND 1000000000000000");
            });

        migrationBuilder.CreateTable(
            name: "task",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                StreamId = table.Column<long>(type: "bigint", nullable: false),
                Code = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Description = table.Column<string>(type: "text", nullable: true),
                Goal = table.Column<int>(type: "integer", nullable: false),
                Periodicity = table.Column<int>(type: "integer", nullable: false),
                StreamPoints = table.Column<int>(type: "integer", nullable: false),
                Audience = table.Column<Audience>(type: "jsonb", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_task", x => x.Id);
                table.CheckConstraint("CK_task_goal", "\"Goal\" BETWEEN 1 AND 1000000000");
            });

        migrationBuilder.CreateTable(
            name: "wallet_balance",
            columns: table => new
            {
                WalletId = table.Column<long>(type: "bigint", nullable: false),
                ResourceId = table.Column<long>(type: "bigint", nullable: false),
                Balance = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wallet_balance", x => new { x.WalletId, x.ResourceId });
                table.CheckConstraint("CK_wallet_balance_nonneg", "\"Balance\" BETWEEN 0 AND 1000000000000000");
            });

        migrationBuilder.CreateTable(
            name: "WalletMovements",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                WalletId = table.Column<long>(type: "bigint", nullable: false),
                OperationId = table.Column<long>(type: "bigint", nullable: false),
                ResourceId = table.Column<long>(type: "bigint", nullable: false),
                Delta = table.Column<long>(type: "bigint", nullable: false),
                BalanceAfter = table.Column<long>(type: "bigint", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                CampaignId = table.Column<long>(type: "bigint", nullable: true),
                OriginalOperationId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WalletMovements", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Wallets",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EmployeeId = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Wallets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "campaign_resource",
            columns: table => new
            {
                CampaignId = table.Column<long>(type: "bigint", nullable: false),
                ResourceId = table.Column<long>(type: "bigint", nullable: false),
                AllocationRequested = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_campaign_resource", x => new { x.CampaignId, x.ResourceId });
                table.ForeignKey(
                    name: "FK_campaign_resource_Campaigns_CampaignId",
                    column: x => x.CampaignId,
                    principalTable: "Campaigns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "operation_line",
            columns: table => new
            {
                OperationId = table.Column<long>(type: "bigint", nullable: false),
                ResourceId = table.Column<long>(type: "bigint", nullable: false),
                Amount = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operation_line", x => new { x.OperationId, x.ResourceId });
                table.ForeignKey(
                    name: "FK_operation_line_Operations_OperationId",
                    column: x => x.OperationId,
                    principalTable: "Operations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "task_reward",
            columns: table => new
            {
                TaskId = table.Column<long>(type: "bigint", nullable: false),
                ResourceId = table.Column<long>(type: "bigint", nullable: false),
                Amount = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_task_reward", x => new { x.TaskId, x.ResourceId });
                table.ForeignKey(
                    name: "FK_task_reward_task_TaskId",
                    column: x => x.TaskId,
                    principalTable: "task",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql("CREATE UNIQUE INDEX \"uq_achievement_company_lower_code\" ON \"Achievements\" (\"CompanyId\", lower(\"Code\"));");

        migrationBuilder.CreateIndex(
            name: "IX_AuditEntries_Target_OccurredAt",
            table: "AuditEntries",
            columns: new[] { "Target", "OccurredAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Campaigns_CompanyId_Season",
            table: "Campaigns",
            columns: new[] { "CompanyId", "Season" });

        migrationBuilder.CreateIndex(
            name: "IX_Campaigns_State",
            table: "Campaigns",
            column: "State");

        migrationBuilder.Sql("CREATE UNIQUE INDEX \"uq_campaign_company_season_lower_code\" ON \"Campaigns\" (\"CompanyId\", \"Season\", lower(\"Code\"));");

        migrationBuilder.CreateIndex(
            name: "IX_Employees_CompanyId_IsActive",
            table: "Employees",
            columns: new[] { "CompanyId", "IsActive" });

        migrationBuilder.CreateIndex(
            name: "IX_Employees_CompanyId_MasterId",
            table: "Employees",
            columns: new[] { "CompanyId", "MasterId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_IntegrationGrants_IntegrationId",
            table: "IntegrationGrants",
            column: "IntegrationId");

        migrationBuilder.CreateIndex(
            name: "IX_Integrations_CompanyId_ServiceSub",
            table: "Integrations",
            columns: new[] { "CompanyId", "ServiceSub" });

        migrationBuilder.CreateIndex(
            name: "IX_Milestones_StreamId_Threshold",
            table: "Milestones",
            columns: new[] { "StreamId", "Threshold" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OperationRequests_CompanyId_InitiatorKey_Kind_SourceNo",
            table: "OperationRequests",
            columns: new[] { "CompanyId", "InitiatorKey", "Kind", "SourceNo" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Operations_CampaignId",
            table: "Operations",
            column: "CampaignId");

        migrationBuilder.CreateIndex(
            name: "IX_Operations_CompanyId_InitiatorKey_Kind_SourceNo",
            table: "Operations",
            columns: new[] { "CompanyId", "InitiatorKey", "Kind", "SourceNo" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Operations_EmployeeId_OccurredAt",
            table: "Operations",
            columns: new[] { "EmployeeId", "OccurredAt" });

        migrationBuilder.CreateIndex(
            name: "uq_cancellation_one_success",
            table: "Operations",
            column: "OriginalOperationId",
            unique: true,
            filter: "\"OriginalOperationId\" IS NOT NULL AND \"Result\" = 0 AND \"Kind\" IN (4, 5)");

        migrationBuilder.CreateIndex(
            name: "IX_progress_EmployeeId_PeriodStart",
            table: "progress",
            columns: new[] { "EmployeeId", "PeriodStart" });

        migrationBuilder.CreateIndex(
            name: "IX_ProgressEvents_CompanyId_SourceId_SourceEventNo",
            table: "ProgressEvents",
            columns: new[] { "CompanyId", "SourceId", "SourceEventNo" },
            unique: true);

        migrationBuilder.Sql("CREATE UNIQUE INDEX \"uq_resource_company_lower_code\" ON \"Resources\" (\"CompanyId\", lower(\"Code\"));");

        migrationBuilder.CreateIndex(
            name: "IX_StatementDatas_StatementId",
            table: "StatementDatas",
            column: "StatementId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_StatementDownloads_StatementId",
            table: "StatementDownloads",
            column: "StatementId");

        migrationBuilder.CreateIndex(
            name: "IX_Statements_OwnerActorType_OwnerActorId_State",
            table: "Statements",
            columns: new[] { "OwnerActorType", "OwnerActorId", "State" });

        migrationBuilder.Sql("CREATE UNIQUE INDEX \"uq_stream_campaign_lower_code\" ON \"stream\" (\"CampaignId\", lower(\"Code\"));");

        migrationBuilder.Sql("CREATE UNIQUE INDEX \"uq_task_stream_lower_code\" ON \"task\" (\"StreamId\", lower(\"Code\"));");

        migrationBuilder.CreateIndex(
            name: "IX_WalletMovements_ResourceId",
            table: "WalletMovements",
            column: "ResourceId");

        migrationBuilder.CreateIndex(
            name: "IX_WalletMovements_WalletId_OccurredAt",
            table: "WalletMovements",
            columns: new[] { "WalletId", "OccurredAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Wallets_EmployeeId",
            table: "Wallets",
            column: "EmployeeId",
            unique: true);
        migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"uq_integration_grant\" ON \"IntegrationGrants\" " +
                "(\"IntegrationId\", \"Kind\", COALESCE(\"CampaignId\", 0), COALESCE(\"ResourceId\", 0), COALESCE(\"PurchaseSystemId\", 0));");
        migrationBuilder.Sql(
            "CREATE OR REPLACE FUNCTION enforce_progress_goal() RETURNS trigger AS $$ " +
            "BEGIN " +
            "  IF NEW.\"Value\" > (SELECT \"Goal\" FROM task WHERE \"Id\" = NEW.\"TaskId\") THEN " +
            "    RAISE EXCEPTION 'progress value exceeds task goal'; " +
            "  END IF; " +
            "  RETURN NEW; " +
            "END; $$ LANGUAGE plpgsql;");
        migrationBuilder.Sql(
            "CREATE TRIGGER trg_progress_goal BEFORE INSERT OR UPDATE ON progress " +
            "FOR EACH ROW EXECUTE FUNCTION enforce_progress_goal();");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "achievement_grant");

        migrationBuilder.DropTable(
            name: "Achievements");

        migrationBuilder.DropTable(
            name: "AuditEntries");

        migrationBuilder.DropTable(
            name: "budget");

        migrationBuilder.DropTable(
            name: "campaign_resource");

        migrationBuilder.DropTable(
            name: "challenge_score");

        migrationBuilder.DropTable(
            name: "Challenges");

        migrationBuilder.DropTable(
            name: "Companies");

        migrationBuilder.DropTable(
            name: "completion");

        migrationBuilder.DropTable(
            name: "Employees");

        migrationBuilder.DropTable(
            name: "IntegrationGrants");

        migrationBuilder.DropTable(
            name: "Integrations");

        migrationBuilder.DropTable(
            name: "Milestones");

        migrationBuilder.DropTable(
            name: "operation_line");

        migrationBuilder.DropTable(
            name: "OperationRequests");

        migrationBuilder.DropTable(
            name: "progress");

        migrationBuilder.DropTable(
            name: "ProgressEvents");

        migrationBuilder.DropTable(
            name: "PurchaseSystems");

        migrationBuilder.DropTable(
            name: "Resources");

        migrationBuilder.DropTable(
            name: "StatementAttempts");

        migrationBuilder.DropTable(
            name: "StatementDatas");

        migrationBuilder.DropTable(
            name: "StatementDownloads");

        migrationBuilder.DropTable(
            name: "Statements");

        migrationBuilder.DropTable(
            name: "stream");

        migrationBuilder.DropTable(
            name: "stream_score");

        migrationBuilder.DropTable(
            name: "task_reward");

        migrationBuilder.DropTable(
            name: "wallet_balance");

        migrationBuilder.DropTable(
            name: "WalletMovements");

        migrationBuilder.DropTable(
            name: "Wallets");

        migrationBuilder.DropTable(
            name: "Campaigns");

        migrationBuilder.DropTable(
            name: "Operations");

        migrationBuilder.DropTable(
            name: "task");
    }
}
