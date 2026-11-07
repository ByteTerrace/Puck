using Puck.SdfVm;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class SettledFrameRatesLawTests {
    [Fact]
    public void ASettledCaptureRatesNoWork() {
        foreach (var rates in Enum.GetValues<SdfUpdateRates>()) {
            Assert.Equal(SdfUpdateRates.None, WorldFramePresenter.RatesOf(rates, settles: true));
            Assert.Equal(rates, WorldFramePresenter.RatesOf(rates, settles: false));
        }
    }
}
