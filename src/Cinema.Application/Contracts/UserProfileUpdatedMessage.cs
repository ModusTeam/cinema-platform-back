namespace Cinema.Application.Common.Contracts;

public record UserProfileUpdatedPayload(
    Guid UserId,
    DateTime? DateOfBirth
);

public record UserProfileUpdatedMessage(
    string Pattern,
    UserProfileUpdatedPayload Data
);
