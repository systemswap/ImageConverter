using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>
/// The donate button above "Drucken" and "Exportieren": opens the donation page in the browser of
/// the system, only on the click.
/// </summary>
public class DonateTests
{
    [Fact]
    public void DonateButton_OpensTheDonationPage_OnlyOnClick()
    {
        var vm = new MainViewModel();
        var opened = new List<string>();
        vm.OpenAddress = address =>
        {
            opened.Add(address);
            return true;
        };

        Assert.Empty(opened);
        vm.DonateCommand.Execute(null);
        Assert.Equal(["https://ko-fi.com/systemswap"], opened);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void WithoutBrowser_TheAddressIsShown()
    {
        var vm = new MainViewModel { OpenAddress = _ => false };
        vm.DonateCommand.Execute(null);
        Assert.Contains(MainViewModel.DonationAddress, vm.ErrorMessage);
    }
}
