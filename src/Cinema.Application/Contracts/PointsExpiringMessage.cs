namespace Cinema.Application.Common.Contracts;

public record PointsExpiringPayload(
    Guid UserId,
    int Points,
    DateTime ExpiresAt
);

public record PointsExpiringMessage(
    string Pattern,
    PointsExpiringPayload Data
);