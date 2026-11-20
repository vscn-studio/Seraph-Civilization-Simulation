using Newtonsoft.Json;
using ProtoBuf;
using SeraphCivilizationSimulation;

internal static class RectangularTerritoryChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var settings=new RectangularTerritoryConfig(); settings.Validate();
        var a=new TerritoryRectangle { MinX=0,MinZ=0,MaxX=9,MaxZ=7 };
        check(a.Pad(8).Area==624,"10x8 exterior must create a 26x24 initial rectangle.");
        check(a.Adjacent(new() {MinX=18,MaxX=21,MinZ=0,MaxZ=3},8),"Eight empty side-gap blocks rejected.");
        check(!a.Adjacent(new() {MinX=19,MaxX=22,MinZ=0,MaxZ=3},8),"Nine-block gap accepted.");
        check(!a.Adjacent(new() {MinX=10,MaxX=13,MinZ=8,MaxZ=11},8),"Diagonal-only expansion accepted.");
        BuildingTerritoryRegion Room(int y,int width=4,int length=4) => BuildingTerritoryDetector.Make("room",new(0,y+1,0),
            [new(0,y+1,0)],[],from x in Enumerable.Range(0,width) from z in Enumerable.Range(0,length) select new TerritoryCell(x,y,z));
        var first=Room(100); var upper=Room(110);
        var site=new RectangularTerritorySite {Bounds=a.Pad(8),Heights=Enumerable.Repeat(100,624).ToArray(),InitialArea=624};
        check(RectangularTerritoryRules.Footprint([first,upper])==16 && RectangularTerritoryRules.Support(site,[first,upper],settings)==624,
            "Stacked floors multiplied area support or failed initial-area floor.");
        check(RectangularTerritoryRules.Qualifies(first,settings) && !RectangularTerritoryRules.Qualifies(Room(100,2,8),settings)
            && !RectangularTerritoryRules.Qualifies(Room(100,3,3),settings),"Expansion room area/side qualification failed.");
        var sparse=Room(100); sparse.Floors=TerritoryGeometry.Encode(first.FloorCells.Append(new(9,100,9))); sparse.Invalidate();
        check(!RectangularTerritoryRules.Qualifies(sparse,settings),"Sparse footprint qualified.");
        var restored=JsonConvert.DeserializeObject<RectangularTerritorySite>(JsonConvert.SerializeObject(site))!;
        check(restored.Height(0,0)==100 && restored.Contains(new(0,84,0),0) && !restored.Contains(new(0,83,0),0),"Persisted height band changed.");
        using var stream=new MemoryStream();
        Serializer.Serialize(stream,new CivilizationSnapshotPacket { TerritorySites=[new() { Id="main",Bounds=a,Area=80,SupportLimit=624,CooldownSeconds=1800 }],
            TerritoryCandidates=[new() { Bounds=a,PreviousArea=40,Ready=true,ReleasedRegions=["old"] }] });
        stream.Position=0; var packet=Serializer.Deserialize<CivilizationSnapshotPacket>(stream);
        check(packet.TerritorySites.Single().Bounds.Area==80 && packet.TerritorySites.Single().CooldownSeconds==1800
            && packet.TerritoryCandidates.Single().Ready && packet.TerritoryCandidates.Single().ReleasedRegions.Single()=="old", "Rectangle wire fields were lost.");
        foreach (var change in new Action<RectangularTerritoryConfig>[] { c=>c.InitialPadding=0,c=>c.MaximumSide=1000,c=>c.MinimumFloorCoverage=double.NaN,c=>c.StableMinutes=-1 })
        {
            var invalid=new RectangularTerritoryConfig(); change(invalid);
            bool rejected=false; try {invalid.Validate();} catch(ArgumentException) {rejected=true;}
            check(rejected,"Invalid rectangle configuration accepted.");
        }
    }
}
