using LiveStudio.Cloud.Security;

namespace LiveStudio.Core.Tests;

public sealed class ChineseIdentityErrorDescriberTests
{
    [Fact]
    public void PasswordPolicyErrorsAreChineseAndActionable()
    {
        var describer = new ChineseIdentityErrorDescriber();

        Assert.Equal("密码至少需要一个特殊符号，例如 !、@ 或 #。", describer.PasswordRequiresNonAlphanumeric().Description);
        Assert.Equal("密码至少需要一个大写英文字母（A–Z）。", describer.PasswordRequiresUpper().Description);
        Assert.Equal("密码至少需要一个数字（0–9）。", describer.PasswordRequiresDigit().Description);
    }
}
