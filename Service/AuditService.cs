using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;
using Nomba_Hackathon.Models;

namespace Nomba_Hackathon.Service;

public class AuditService
{
    private readonly LedgerDbContext _db;
    private readonly ILogger<AuditService> _logger;

    public AuditService(LedgerDbContext db, ILogger<AuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordKycTierChangeAsync(
        string customerId,
        int oldTier,
        int newTier,
        string reason = "",
        string changedBy = "system")
    {
        if (oldTier == newTier)
            return;

        var auditLog = new KycTierAuditLog
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            OldTier = oldTier,
            NewTier = newTier,
            Reason = reason,
            ChangedBy = changedBy,
            ChangedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.KycTierAuditLogs.Add(auditLog);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "KYC tier changed for customer {CustomerId}: {OldTier} -> {NewTier} (reason: {Reason})",
            customerId, oldTier, newTier, reason);
    }

    public async Task RecordIdentityChangeAsync(
        string entityId,
        string entityType,
        string fieldName,
        string? oldValue,
        string? newValue,
        string changedBy = "system")
    {
        if (oldValue == newValue)
            return;

        var auditLog = new IdentityAuditLog
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = entityType,
            FieldName = fieldName,
            OldValue = oldValue,
            NewValue = newValue,
            ChangedBy = changedBy,
            ChangedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.IdentityAuditLogs.Add(auditLog);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Identity change for {EntityType} {EntityId}.{FieldName}: '{OldValue}' -> '{NewValue}'",
            entityType, entityId, fieldName, oldValue ?? "null", newValue ?? "null");
    }

    public async Task<List<KycTierAuditLog>> GetKycTierHistoryAsync(
        string customerId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var query = _db.KycTierAuditLogs.AsQueryable();

        if (from.HasValue)
            query = query.Where(l => l.ChangedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(l => l.ChangedAt <= to.Value);

        return await query
            .Where(l => l.CustomerId == customerId)
            .OrderByDescending(l => l.ChangedAt)
            .ToListAsync();
    }

    public async Task<List<IdentityAuditLog>> GetIdentityHistoryAsync(
        string entityId,
        string? entityType = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var query = _db.IdentityAuditLogs.AsQueryable();

        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(l => l.EntityType == entityType);

        if (from.HasValue)
            query = query.Where(l => l.ChangedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(l => l.ChangedAt <= to.Value);

        return await query
            .Where(l => l.EntityId == entityId)
            .OrderByDescending(l => l.ChangedAt)
            .ToListAsync();
    }
}
