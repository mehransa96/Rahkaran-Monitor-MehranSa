using RSM.Core.Capabilities;

namespace RSM.Tests;

public class CapabilityCatalogTests
{
    [Fact]
    public void Every_capability_id_has_a_bilingual_definition()
    {
        var ids = typeof(CapabilityIds).GetFields().Select(f => (string)f.GetValue(null)!).ToList();

        ids.Should().OnlyHaveUniqueItems();
        foreach (var id in ids)
        {
            var def = CapabilityCatalog.Get(id);
            foreach (var text in new[] { def.Title, def.Enables, def.Remediation })
            {
                text.Fa.Should().NotBeNullOrWhiteSpace(id);
                text.En.Should().NotBeNullOrWhiteSpace(id);
            }
        }

        CapabilityCatalog.All.Select(d => d.Id).Should().BeEquivalentTo(ids);
    }

    [Fact]
    public void Every_state_is_described_in_both_languages()
    {
        foreach (var state in Enum.GetValues<CapabilityState>())
        {
            var text = CapabilityCatalog.DescribeState(state);
            text.Fa.Should().NotBe(state.ToString());
            text.En.Should().NotBeNullOrWhiteSpace();
        }
    }
}
