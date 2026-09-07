using System.Security.Claims;
using System.Threading.RateLimiting;

using backend.main.shared.responses;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace backend.main.application.security
{
    public static class RateLimiterConfiguration
    {
        private const int PermitLimit = 100;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        public const string AuthPolicyName = "auth";
        private const int AuthPermitLimit = 10;
        private static readonly TimeSpan AuthWindow = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Policy for the anonymous username availability probe. It is called once per pause in
        /// typing, so the auth policy's 10-per-5-minutes would break the signup form long before
        /// it inconvenienced anyone enumerating names; this window is sized for a person filling
        /// in a field while still bounding how fast the namespace can be walked.
        /// </summary>
        public const string UsernameAvailabilityPolicyName = "username-availability";
        private const int UsernameAvailabilityPermitLimit = 30;
        private static readonly TimeSpan UsernameAvailabilityWindow = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Policy for the anonymous email availability probe. Deliberately half the username
        /// budget: a username namespace has to be walked to be mined, whereas emails are tested
        /// against a list the attacker already holds, so every permitted request is a usable
        /// answer about a real address. This limit is the only thing bounding that, and one probe
        /// per typed address is all the signup form needs.
        /// </summary>
        public const string EmailAvailabilityPolicyName = "email-availability";
        private const int EmailAvailabilityPermitLimit = 15;
        private static readonly TimeSpan EmailAvailabilityWindow = TimeSpan.FromMinutes(1);

        public static IServiceCollection AddInMemoryRateLimiter(
            this IServiceCollection services,
            IConfiguration? configuration = null)
        {
            var permitLimit = configuration?.GetValue<int?>("RateLimiter:PermitLimit")
                ?? PermitLimit;
            var authPermitLimit = configuration?.GetValue<int?>("RateLimiter:AuthPermitLimit")
                ?? AuthPermitLimit;
            var usernameAvailabilityPermitLimit =
                configuration?.GetValue<int?>("RateLimiter:UsernameAvailabilityPermitLimit")
                ?? UsernameAvailabilityPermitLimit;
            var emailAvailabilityPermitLimit =
                configuration?.GetValue<int?>("RateLimiter:EmailAvailabilityPermitLimit")
                ?? EmailAvailabilityPermitLimit;

            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                options.OnRejected = async (context, cancellationToken) =>
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.HttpContext.Response.ContentType = "application/json";
                    await context.HttpContext.Response.WriteAsJsonAsync(
                        ApiResponse<object?>.Failure(
                            "Rate limit exceeded. Please try again later.",
                            "TOO_MANY_REQUESTS"
                        ),
                        cancellationToken
                    );
                };

                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    try
                    {
                        string partitionKey = GetPartitionKey(context);
                        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitLimit,
                            Window = Window,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0
                        });
                    }
                    catch
                    {
                        return RateLimitPartition.GetFixedWindowLimiter("fail-closed", _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 0,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        });
                    }
                });

                options.AddPolicy(AuthPolicyName, context =>
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"auth:{ip}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = authPermitLimit,
                        Window = AuthWindow,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
                });

                options.AddPolicy(UsernameAvailabilityPolicyName, context =>
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"username-availability:{ip}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = usernameAvailabilityPermitLimit,
                            Window = UsernameAvailabilityWindow,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0
                        });
                });

                options.AddPolicy(EmailAvailabilityPolicyName, context =>
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"email-availability:{ip}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = emailAvailabilityPermitLimit,
                            Window = EmailAvailabilityWindow,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0
                        });
                });
            });

            return services;
        }

        private static string GetPartitionKey(HttpContext context)
        {
            if (context.User?.Identity?.IsAuthenticated == true)
            {
                var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(userId))
                    return $"user:{userId}";
            }

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return $"ip:{ip}";
        }
    }
}
