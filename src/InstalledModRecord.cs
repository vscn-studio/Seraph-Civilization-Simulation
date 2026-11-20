using ProtoBuf;

namespace SeraphCivilizationSimulation;

[ProtoContract]
public sealed class InstalledModRecord
{
    [ProtoMember(1)] public string ModId { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public string Version { get; set; } = "";
    [ProtoMember(4)] public string Authors { get; set; } = "";
    [ProtoMember(5)] public string Description { get; set; } = "";
    [ProtoMember(6)] public string Side { get; set; } = "";
}

