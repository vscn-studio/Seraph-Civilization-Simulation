using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SeraphCivilizationSimulation;

internal static class EconomyGuiFingerprint
{
    // Each display includes a bounded state fingerprint and durability range, excluding
    // natural ageing and temperature. Do not base64-copy the full inventory just to detect
    // a UI change, or rebuild a list whenever food ages in the player's backpack.
    private sealed class DisplayContract : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization serialization)
        {
            var property = base.CreateProperty(member, serialization);
            if (member.DeclaringType == typeof(EconomyItemView) && member.Name == nameof(EconomyItemView.Stack)
                || member.DeclaringType == typeof(EconomyExchangeItem) && member.Name == nameof(EconomyExchangeItem.Requirement)
                || member.Name == "Message" && member.DeclaringType is { } type
                    && (type == typeof(EconomySnapshot) || type == typeof(EconomyTradeView))) property.Ignored = true;
            return property;
        }
    }
    private static readonly JsonSerializerSettings Settings = new() { ContractResolver = new DisplayContract() };
    public static string Of(object value) => JsonConvert.SerializeObject(value, Settings);
}
