namespace Cinema.Application.Achievements.Dtos;

public enum AchievementCategory
{
    Unspecified = 0,
    Visits = 1,
    Spending = 2,
    Tier = 3,
    Time = 4,
    Special = 5,
    Streak = 6,
    Secret = 7
}

public enum AchievementRarity
{
    Unspecified = 0,
    Common = 1,
    Uncommon = 2,
    Rare = 3,
    Epic = 4,
    Legendary = 5
}

public enum AchievementStrategy
{
    Unspecified = 0,
    Instant = 1,
    Threshold = 2,
    Streak = 3
}

public class AchievementDto
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SecretHint { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public string Icon { get; set; } = string.Empty;
    public AchievementCategory Category { get; set; }
    public AchievementRarity Rarity { get; set; }
    public AchievementStrategy Strategy { get; set; }
    public string CriteriaJson { get; set; } = string.Empty;
    public int RewardPoints { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public class UserAchievementDto
{
    public AchievementDto Achievement { get; set; } = null!;
    public int Current { get; set; }
    public int Target { get; set; }
    public bool IsUnlocked { get; set; }
    public string UnlockedAt { get; set; } = string.Empty;
}

public class GetAdminAchievementsResponse
{
    public List<AchievementDto> Achievements { get; set; } = new();
    public int Total { get; set; }
}

public class GetUserAchievementsResponse
{
    public List<UserAchievementDto> Achievements { get; set; } = new();
}

public class CreateAchievementDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SecretHint { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public string Icon { get; set; } = string.Empty;
    public AchievementCategory Category { get; set; }
    public AchievementRarity Rarity { get; set; }
    public AchievementStrategy Strategy { get; set; }
    public string CriteriaJson { get; set; } = string.Empty;
    public int RewardPoints { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class UpdateAchievementDto
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SecretHint { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public string Icon { get; set; } = string.Empty;
    public AchievementCategory Category { get; set; }
    public AchievementRarity Rarity { get; set; }
    public AchievementStrategy Strategy { get; set; }
    public string CriteriaJson { get; set; } = string.Empty;
    public int RewardPoints { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}
