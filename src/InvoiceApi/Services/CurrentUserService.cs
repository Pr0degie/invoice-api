using System.Security.Claims;
using InvoiceApi.Exceptions;

namespace InvoiceApi.Services;

public interface ICurrentUserService
{
    Guid CurrentUserId { get; }
}

public class CurrentUserService(IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    public Guid CurrentUserId
    {
        get
        {
            var sub = httpContextAccessor.HttpContext?.User.FindFirstValue("sub")
                ?? throw new UnauthorizedException("Not authenticated.");
            return Guid.Parse(sub);
        }
    }
}
