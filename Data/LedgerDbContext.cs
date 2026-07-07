using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Models;

namespace Nomba_Hackathon.Data;

// EF Core context mapped to the existing PostgreSQL schema (SQL/Data.sql).
// Postgres folds unquoted identifiers to lowercase, so table/column names
// are mapped explicitly to match the raw DDL.
public class LedgerDbContext : DbContext
{
    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options)
    {
    }

    public DbSet<TransactionRecord> Transactions => Set<TransactionRecord>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<VirtualAccount> VirtualAccounts => Set<VirtualAccount>();
    public DbSet<MisdirectedPayment> MisdirectedPayments => Set<MisdirectedPayment>();
    public DbSet<ReconciliationException> ReconciliationExceptions => Set<ReconciliationException>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<PaymentPlan> PaymentPlans => Set<PaymentPlan>();
    public DbSet<KycTierAuditLog> KycTierAuditLogs => Set<KycTierAuditLog>();
    public DbSet<IdentityAuditLog> IdentityAuditLogs => Set<IdentityAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransactionRecord>(entity =>
        {
            entity.ToTable("transactions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ReferenceCode).HasColumnName("reference_code").HasMaxLength(100);
            entity.HasIndex(e => e.ReferenceCode).IsUnique();
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(50);
            entity.Property(e => e.AccountId).HasColumnName("account_id").HasMaxLength(100);
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("decimal(18,2)");
            entity.Property(e => e.PayerName).HasColumnName("payer_name").HasMaxLength(255);
            entity.Property(e => e.MatchConfidence).HasColumnName("match_confidence").HasColumnType("decimal(5,4)");
            entity.Property(e => e.MatchType).HasColumnName("match_type").HasMaxLength(20);
            entity.Property(e => e.PaymentPlanId).HasColumnName("payment_plan_id");
            entity.Property(e => e.CreatedAt)
                  .HasColumnName("created_at")
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasMany(e => e.Entries)
                  .WithOne(l => l.Transaction!)
                  .HasForeignKey(l => l.TransactionId);
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.ToTable("customers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").HasMaxLength(100);
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(255);
            entity.Property(e => e.Email).HasColumnName("email").HasMaxLength(255);
            entity.Property(e => e.PhoneNumber).HasColumnName("phone_number").HasMaxLength(20);
            entity.Property(e => e.KycTier).HasColumnName("kyc_tier");
            entity.Property(e => e.DailyLimit).HasColumnName("daily_limit").HasColumnType("decimal(18,2)");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.Email).IsUnique().HasDatabaseName("idx_customers_email");
            entity.HasIndex(e => e.Status).HasDatabaseName("idx_customers_status");
        });

        modelBuilder.Entity<VirtualAccount>(entity =>
        {
            entity.ToTable("virtual_accounts");
            entity.HasKey(e => e.AccountRef);
            entity.Property(e => e.AccountRef).HasColumnName("account_ref").HasMaxLength(100);
            entity.Property(e => e.CustomerId).HasColumnName("customer_id").HasMaxLength(100);
            entity.Property(e => e.AccountName).HasColumnName("account_name").HasMaxLength(255);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.KycTier).HasColumnName("kyc_tier");
            entity.Property(e => e.Nuban).HasColumnName("nuban").HasMaxLength(20);
            entity.Property(e => e.BankCode).HasColumnName("bank_code").HasMaxLength(20);
            entity.Property(e => e.BankName).HasColumnName("bank_name").HasMaxLength(255);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.CustomerId).HasDatabaseName("idx_virtual_accounts_customer");
            entity.HasIndex(e => e.Status).HasDatabaseName("idx_virtual_accounts_status");
            entity.HasIndex(e => e.Nuban).HasDatabaseName("idx_virtual_accounts_nuban");

            entity.HasOne(e => e.Customer)
                  .WithMany(c => c.VirtualAccounts)
                  .HasForeignKey(e => e.CustomerId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.ToTable("ledgerentries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.AccountId).HasColumnName("account_id").HasMaxLength(100);
            entity.Property(e => e.DebitAmount).HasColumnName("debit_amount").HasColumnType("decimal(18,2)");
            entity.Property(e => e.CreditAmount).HasColumnName("credit_amount").HasColumnType("decimal(18,2)");
            entity.Property(e => e.EntryType).HasColumnName("entry_type").HasMaxLength(50);
            entity.Property(e => e.CreatedAt)
                  .HasColumnName("created_at")
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.AccountId).HasDatabaseName("idx_ledger_account");
        });

        modelBuilder.Entity<MisdirectedPayment>(entity =>
        {
            entity.ToTable("misdirected_payments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.TransactionRef).HasColumnName("transaction_ref").HasMaxLength(100);
            entity.Property(e => e.ReceivedAccountRef).HasColumnName("received_account_ref").HasMaxLength(100);
            entity.Property(e => e.IntendedCustomerId).HasColumnName("intended_customer_id").HasMaxLength(100);
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("decimal(18,2)");
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(255);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.ResolutionNote).HasColumnName("resolution_note").HasMaxLength(500);

            entity.HasIndex(e => e.TransactionRef).HasDatabaseName("idx_misdirected_ref");
            entity.HasIndex(e => e.Status).HasDatabaseName("idx_misdirected_status");
            entity.HasIndex(e => e.ReceivedAccountRef).HasDatabaseName("idx_misdirected_account");
        });

        modelBuilder.Entity<ReconciliationException>(entity =>
        {
            entity.ToTable("reconciliation_exceptions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.TransactionRef).HasColumnName("transaction_ref").HasMaxLength(100);
            entity.Property(e => e.ExceptionType).HasColumnName("exception_type").HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message").HasMaxLength(500);
            entity.Property(e => e.AiDiagnosis).HasColumnName("ai_diagnosis").HasColumnType("text");
            entity.Property(e => e.AiRecommendation).HasColumnName("ai_recommendation").HasColumnType("text");
            entity.Property(e => e.AiConfidence).HasColumnName("ai_confidence").HasColumnType("decimal(3,2)");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by").HasMaxLength(255);
            entity.Property(e => e.ResolutionAction).HasColumnName("resolution_action").HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.ContextData).HasColumnName("context_data").HasColumnType("jsonb");

            entity.HasIndex(e => e.TransactionRef).HasDatabaseName("idx_exceptions_ref");
            entity.HasIndex(e => e.Status).HasDatabaseName("idx_exceptions_status");
        });

        modelBuilder.Entity<WebhookEvent>(entity =>
        {
            entity.ToTable("webhook_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(100);
            entity.Property(e => e.AccountRef).HasColumnName("account_ref").HasMaxLength(100);
            entity.Property(e => e.CustomerId).HasColumnName("customer_id").HasMaxLength(100);
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(e => e.RetryCount).HasColumnName("retry_count");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.DeliveredAt).HasColumnName("delivered_at");
            entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(500);

            entity.HasIndex(e => e.Status).HasDatabaseName("idx_webhook_events_status");
            entity.HasIndex(e => e.AccountRef).HasDatabaseName("idx_webhook_events_account");
        });

        modelBuilder.Entity<WebhookSubscription>(entity =>
        {
            entity.ToTable("webhook_subscriptions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Url).HasColumnName("url").HasMaxLength(500);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.Secret).HasColumnName("secret").HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.LastDeliveryAt).HasColumnName("last_delivery_at");
            entity.Property(e => e.SuccessCount).HasColumnName("success_count");
            entity.Property(e => e.FailureCount).HasColumnName("failure_count");

            entity.HasIndex(e => e.Status).HasDatabaseName("idx_webhook_subscriptions_status");
            entity.HasIndex(e => e.Url).IsUnique().HasDatabaseName("idx_webhook_subscriptions_url");
        });

        modelBuilder.Entity<PaymentPlan>(entity =>
        {
            entity.ToTable("payment_plans");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TransactionRef).HasColumnName("transaction_ref").HasMaxLength(100);
            entity.Property(e => e.AccountRef).HasColumnName("account_ref").HasMaxLength(100);
            entity.Property(e => e.TotalExpected).HasColumnName("total_expected").HasColumnType("decimal(18,2)");
            entity.Property(e => e.TotalReceived).HasColumnName("total_received").HasColumnType("decimal(18,2)");
            entity.Property(e => e.RemainingBalance).HasColumnName("remaining_balance").HasColumnType("decimal(18,2)");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.InstallmentsReceived).HasColumnName("installments_received");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.LastPaymentAt).HasColumnName("last_payment_at");

            entity.HasIndex(e => e.TransactionRef).HasDatabaseName("idx_payment_plans_ref");
            entity.HasIndex(e => e.Status).HasDatabaseName("idx_payment_plans_status");
            entity.HasIndex(e => e.AccountRef).HasDatabaseName("idx_payment_plans_account");
        });

        modelBuilder.Entity<KycTierAuditLog>(entity =>
        {
            entity.ToTable("kyc_tier_audit_logs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id").HasMaxLength(100);
            entity.Property(e => e.OldTier).HasColumnName("old_tier");
            entity.Property(e => e.NewTier).HasColumnName("new_tier");
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(500);
            entity.Property(e => e.ChangedBy).HasColumnName("changed_by").HasMaxLength(255);
            entity.Property(e => e.ChangedAt).HasColumnName("changed_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.CustomerId).HasDatabaseName("idx_kyc_audit_customer");
            entity.HasIndex(e => e.ChangedAt).HasDatabaseName("idx_kyc_audit_timestamp");

            entity.HasOne(e => e.Customer)
                  .WithMany()
                  .HasForeignKey(e => e.CustomerId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IdentityAuditLog>(entity =>
        {
            entity.ToTable("identity_audit_logs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.EntityId).HasColumnName("entity_id").HasMaxLength(100);
            entity.Property(e => e.EntityType).HasColumnName("entity_type").HasMaxLength(50);
            entity.Property(e => e.FieldName).HasColumnName("field_name").HasMaxLength(100);
            entity.Property(e => e.OldValue).HasColumnName("old_value").HasColumnType("text");
            entity.Property(e => e.NewValue).HasColumnName("new_value").HasColumnType("text");
            entity.Property(e => e.ChangedBy).HasColumnName("changed_by").HasMaxLength(255);
            entity.Property(e => e.ChangedAt).HasColumnName("changed_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.EntityId).HasDatabaseName("idx_identity_audit_entity");
            entity.HasIndex(e => e.EntityType).HasDatabaseName("idx_identity_audit_type");
            entity.HasIndex(e => e.ChangedAt).HasDatabaseName("idx_identity_audit_timestamp");
        });
    }
}
