namespace Cinema.Application.Common.Contracts;

public record TierUpgradedPayload(
    Guid UserId,
    string OldTier,
    string NewTier,
    DateTime UpgradedAt
);

public record TierUpgradedMessage(
    string Pattern,
    TierUpgradedPayload Data
);
