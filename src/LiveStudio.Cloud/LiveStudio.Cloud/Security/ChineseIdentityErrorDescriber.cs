using Microsoft.AspNetCore.Identity;

namespace LiveStudio.Cloud.Security;

public sealed class ChineseIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => Error("发生未知错误，请重试。");

    public override IdentityError ConcurrencyFailure() => Error("账户信息已被修改，请刷新后重试。");

    public override IdentityError PasswordMismatch() => Error("当前密码不正确。");

    public override IdentityError InvalidToken() => Error("验证信息无效或已经过期。");

    public override IdentityError LoginAlreadyAssociated() => Error("这个登录方式已经绑定到其他账户。");

    public override IdentityError InvalidUserName(string? userName) =>
        Error($"用户名“{userName}”无效。");

    public override IdentityError InvalidEmail(string? email) =>
        Error($"邮箱“{email}”格式无效。");

    public override IdentityError DuplicateUserName(string userName) =>
        Error($"用户名“{userName}”已经存在。");

    public override IdentityError DuplicateEmail(string email) =>
        Error($"邮箱“{email}”已经注册。");

    public override IdentityError InvalidRoleName(string? role) =>
        Error($"角色“{role}”无效。");

    public override IdentityError DuplicateRoleName(string role) =>
        Error($"角色“{role}”已经存在。");

    public override IdentityError UserAlreadyHasPassword() => Error("这个账户已经设置了密码。");

    public override IdentityError UserLockoutNotEnabled() => Error("这个账户没有启用锁定功能。");

    public override IdentityError UserAlreadyInRole(string role) => Error($"账户已经属于角色“{role}”。");

    public override IdentityError UserNotInRole(string role) => Error($"账户不属于角色“{role}”。");

    public override IdentityError PasswordTooShort(int length) => Error($"密码至少需要 {length} 个字符。");

    public override IdentityError PasswordRequiresNonAlphanumeric() => Error("密码至少需要一个特殊符号，例如 !、@ 或 #。");

    public override IdentityError PasswordRequiresDigit() => Error("密码至少需要一个数字（0–9）。");

    public override IdentityError PasswordRequiresLower() => Error("密码至少需要一个小写英文字母（a–z）。");

    public override IdentityError PasswordRequiresUpper() => Error("密码至少需要一个大写英文字母（A–Z）。");

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        Error($"密码至少需要包含 {uniqueChars} 个不同字符。");

    public override IdentityError RecoveryCodeRedemptionFailed() => Error("恢复代码无效或已经使用。");

    private static IdentityError Error(string description) => new() { Description = description };
}
