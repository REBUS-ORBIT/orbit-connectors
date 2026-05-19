using Rhino;
using Rhino.DocObjects;
using Orbit.Objects.Proxies;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Extracts Rhino groups and builds GroupProxy objects for the send pipeline.
/// Should be called once per send, after all objects have been converted.
/// </summary>
public static class RhinoGroupConverter
{
    public static void CollectGroups(RhinoDoc doc, ConversionContext context, IEnumerable<RhinoObject> objects)
    {
        var objectsByGroup = new Dictionary<int, List<string>>();

        foreach (var obj in objects)
        {
            var groupIndices = obj.GetGroupList();
            if (groupIndices == null) continue;

            foreach (var gi in groupIndices)
            {
                if (!objectsByGroup.ContainsKey(gi))
                    objectsByGroup[gi] = new List<string>();
                objectsByGroup[gi].Add(obj.Id.ToString());
            }
        }

        foreach (var (groupIndex, objectIds) in objectsByGroup)
        {
            var group = doc.Groups.FindIndex(groupIndex);
            if (group == null) continue;

            var proxy = new GroupProxy
            {
                ApplicationId = group.Id.ToString(),
                Name      = string.IsNullOrEmpty(group.Name) ? $"Group-{groupIndex}" : group.Name,
                ObjectIds = objectIds
            };

            context.GroupProxies.Add(proxy);
        }
    }
}
