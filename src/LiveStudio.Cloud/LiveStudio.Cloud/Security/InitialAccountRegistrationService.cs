using LiveStudio.Cloud.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Security;

public sealed class InitialAccountRegistrationService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager)
{
    public Task<bool> IsRegistrationOpenAsync(CancellationToken cancellationToken = default) =>
        dbContext.Users.AllAsync(_ => false, cancellationToken);

    public async Task<InitialAccountRegistrationResult> RegisterAsync(
        ApplicationUser user,
        string password,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "LOCK TABLE \"AspNetUsers\" IN SHARE ROW EXCLUSIVE MODE",
            cancellationToken);

        if (await dbContext.Users.AnyAsync(cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new InitialAccountRegistrationResult(
                true,
                IdentityResult.Failed(new IdentityError
                {
                    Code = "InitialAccountAlreadyExists",
                    Description = "管理员账户已建立，请直接登录。"
                }));
        }

        var result = await userManager.CreateAsync(user, password);
        if (result.Succeeded)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new InitialAccountRegistrationResult(false, result);
    }
}

public sealed record InitialAccountRegistrationResult(
    bool RegistrationClosed,
    IdentityResult IdentityResult);
