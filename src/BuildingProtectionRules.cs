namespace SeraphCivilizationSimulation;

public sealed class BuildingProtectionConfig
{
    public bool Enabled { get; set; } = true;
    public double FireProtectionDamagePerSecond { get; set; } = 0.2;
    public double ArrowContactSeconds { get; set; } = 12;
    public double CoolingPerSecond { get; set; } = 0.5;
    public double ManualFireDelayMaximumSeconds { get; set; } = 120;
    public double ManualFireStrengthForMaximum { get; set; } = 200;
    public double GrassIgnitionSeconds { get; set; } = 2;
    public double GrassFailureSeconds { get; set; } = 15;
    public double ClothIgnitionSeconds { get; set; } = 4;
    public double ClothFailureSeconds { get; set; } = 25;
    public double WoodIgnitionSeconds { get; set; } = 45;
    public double WoodFailureSeconds { get; set; } = 120;
    public double TimberIgnitionSeconds { get; set; } = 90;
    public double TimberFailureSeconds { get; set; } = 240;
    public void Validate()
    {
        foreach (double value in new[] { FireProtectionDamagePerSecond, ArrowContactSeconds, CoolingPerSecond,
            ManualFireDelayMaximumSeconds, ManualFireStrengthForMaximum, GrassIgnitionSeconds, GrassFailureSeconds,
            ClothIgnitionSeconds, ClothFailureSeconds, WoodIgnitionSeconds, WoodFailureSeconds, TimberIgnitionSeconds, TimberFailureSeconds })
            if (!double.IsFinite(value) || value <= 0 || value > 86400) throw new ArgumentException("Invalid building fire settings.");
    }
}

public sealed class BuildingFireState
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int Dimension { get; set; }
    public string BlockCode { get; set; } = "";
    public double Heat { get; set; }
    public double DamageSeconds { get; set; }
    public double ManualConsumedFraction { get; set; }
    public bool Ignited { get; set; }
    public string OwnerUid { get; set; } = "";
}

public static class BuildingProtectionRules
{
    public static double RecoveryRate(TerritoryConfig config, int members)
        => Math.Min(Math.Max(0, members) * config.ReinforcementRecoveryPerMemberPerMinute, config.ReinforcementRecoveryMaximumPerMinute);

    public static double Remaining(TerritoryWear wear, int capacity, double now, double ratePerMinute, double delaySeconds)
    {
        double damage = Math.Max(0, wear.Damage + wear.FractionalDamage);
        // Capacity growth is earned gradually. A population change cannot refill a breached block.
        if (wear.Capacity >= 0 && capacity > wear.Capacity) damage += capacity - wear.Capacity;
        damage = Math.Clamp(damage, 0, capacity);
        double elapsed = Math.Max(0, now - Math.Max(wear.LastRecoverySeconds, wear.LastAttackSeconds + delaySeconds));
        damage = Math.Max(0, damage - elapsed * Math.Max(0, ratePerMinute) / 60);
        wear.Capacity = capacity;
        wear.LastRecoverySeconds = now;
        SetDamage(wear, damage);
        return Math.Max(0, capacity - damage);
    }

    public static void SetDamage(TerritoryWear wear, double damage)
    {
        wear.Damage = (int)Math.Floor(Math.Max(0, damage));
        wear.FractionalDamage = Math.Max(0, damage - wear.Damage);
    }

    public static double Consume(TerritoryWear wear, int capacity, double amount, double now)
    {
        double consumed = Math.Min(Math.Max(0, amount), Math.Max(0, capacity - wear.Damage - wear.FractionalDamage));
        SetDamage(wear, wear.Damage + wear.FractionalDamage + consumed);
        wear.LastAttackSeconds = wear.LastRecoverySeconds = now;
        return consumed;
    }
}
