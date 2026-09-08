using System.Reflection;

using backend.main.application.bootstrap;
using backend.main.application.features;
using backend.main.features.auth;
using backend.main.features.auth.captcha;
using backend.main.features.bloom;
using backend.main.features.profile.email;
using backend.main.features.cache;
using backend.main.features.clubs.follow;
using backend.main.features.clubs.follow.invitations;
using backend.main.features.clubs.invitations;
using backend.main.features.clubs.posts.search;
using backend.main.features.clubs.search;
using backend.main.features.events.access;
using backend.main.features.events.favourites;
using backend.main.features.events.invitations;
using backend.main.features.events.registration;
using backend.main.features.events.search;
using backend.main.features.events.waitlist;
using backend.main.features.payment;
using backend.main.infrastructure.database.core;
using backend.main.infrastructure.elasticsearch;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace backend.tests.Unit.Application.Bootstrap;

public class ContainerTests
{
    [Fact]
    public void AddApplicationServices_ShouldRegisterBloomFilters_WhenTheFeatureIsOn()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        services.AddApplicationServices(config, includeHostedServices: false);

        // The concrete registry is registered alongside the interface because the rebuild runner
        // needs its internal publish members, and both must resolve to the same instance.
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(BloomFilterRegistry));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IBloomFilterRegistry));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IBloomFilterSource)
            && descriptor.ImplementationType == typeof(UsernameBloomFilterSource));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IBloomFilterSource)
            && descriptor.ImplementationType == typeof(EmailBloomFilterSource));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(BloomFilterRebuildRunner));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IUsernameAvailabilityService)
            && descriptor.ImplementationType == typeof(UsernameAvailabilityService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEmailAvailabilityService)
            && descriptor.ImplementationType == typeof(EmailAvailabilityService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEmailChangeService)
            && descriptor.ImplementationType == typeof(EmailChangeService));
    }

    /// <summary>
    /// Sources and configured targets must be the same set, in both directions.
    /// </summary>
    /// <remarks>
    /// A source with no matching target fails silently: <c>BloomFilterRebuildRunner</c> warns once
    /// per cycle and every lookup for it degrades to a database query forever, so the filter simply
    /// never turns on. A target with no source is an empty bitmap that answers DefinitelyAbsent for
    /// values that do exist. Both sides are derived from the bound options rather than a literal
    /// list, so adding a target without its source (or the reverse) fails here.
    ///
    /// Deliberately built on the defaults with no configuration supplied: appsettings.json is not
    /// loaded on every host, so the defaults are what has to be self-consistent.
    /// </remarks>
    [Fact]
    public void AddApplicationServices_ShouldRegisterExactlyOneSourcePerConfiguredTarget()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ICacheService, NoOpCacheService>();
        // Sources are scoped and take the DbContext; they only need to be constructible here.
        services.AddDbContext<AppDatabaseContext>(options => options.UseSqlite("Data Source=:memory:"));

        services.AddApplicationServices(config, includeHostedServices: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var configuredTargets = provider.GetRequiredService<IOptions<BloomFilterOptions>>()
            .Value.Targets.Keys;
        var coveredTargets = scope.ServiceProvider.GetServices<IBloomFilterSource>()
            .Select(source => source.Target);

        coveredTargets.Should().BeEquivalentTo(configuredTargets);
    }

    [Fact]
    public void AddApplicationServices_ShouldResolveOneRegistryInstance_ForBothRegistrations()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ICacheService, NoOpCacheService>();

        services.AddApplicationServices(config, includeHostedServices: false);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBloomFilterRegistry>()
            .Should().BeSameAs(provider.GetRequiredService<BloomFilterRegistry>());
    }

    [Fact]
    public void AddApplicationServices_ShouldRegisterTheDisabledRegistry_WhenBloomIsOff()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureFlags:bloom"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        services.AddApplicationServices(config, includeHostedServices: false);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IBloomFilterRegistry)
            && descriptor.ImplementationType == typeof(DisabledBloomFilterRegistry));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IBloomFilterSource));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(BloomFilterRebuildRunner));

        // Still registered: both fall back to the repository whenever the filter cannot answer.
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IUsernameAvailabilityService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEmailAvailabilityService));
    }

    [Fact]
    public void AddApplicationServices_ShouldRegisterTheBloomMaintenanceService_WithHostedServices()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        services.AddApplicationServices(config, includeHostedServices: true);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(BloomFilterMaintenanceService));
    }

    [Fact]
    public void ResolveCaptchaProvider_ShouldHonorExplicitGoogleSetting()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Captcha:Provider"] = "google",
                ["Turnstile:Secret"] = "secret-value"
            })
            .Build();

        InvokeResolveCaptchaProvider(config).Should().Be("google");
    }

    [Fact]
    public void ResolveCaptchaProvider_ShouldHonorExplicitTurnstileSetting()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CAPTCHA_PROVIDER"] = "turnstile"
            })
            .Build();

        InvokeResolveCaptchaProvider(config).Should().Be("turnstile");
    }

    [Fact]
    public void ResolveCaptchaProvider_ShouldInferTurnstile_WhenSecretExists()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TURNSTILE_SECRET"] = "secret-value"
            })
            .Build();

        InvokeResolveCaptchaProvider(config).Should().Be("turnstile");
    }

    [Fact]
    public void ResolveCaptchaProvider_ShouldFallbackToGoogle_WhenTurnstileSecretMissing()
    {
        var config = new ConfigurationBuilder().Build();

        InvokeResolveCaptchaProvider(config).Should().Be("google");
    }

    [Fact]
    public void AddSearchInfrastructure_ShouldRegisterSearchServices_AndCircuitBreaker_WhenSearchEnabled()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddSearchInfrastructure(config);

        services.Any(descriptor =>
            descriptor.ServiceType == typeof(ElasticsearchCircuitBreaker)).Should().BeTrue();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IEventSearchService)
            && descriptor.ImplementationType?.Name == "EventSearchService").Should().BeTrue();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IClubSearchService)
            && descriptor.ImplementationType?.Name == "ClubSearchService").Should().BeTrue();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IClubPostSearchService)
            && descriptor.ImplementationType?.Name == "ClubPostSearchService").Should().BeTrue();
    }

    [Fact]
    public void AddSearchInfrastructure_ShouldRegisterDisabledSearchServices_WhenSearchDisabled()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureFlags:search"] = "false"
            })
            .Build();

        services.AddSearchInfrastructure(config);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventSearchService)
            && descriptor.ImplementationType == typeof(DisabledEventSearchService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IClubSearchService)
            && descriptor.ImplementationType == typeof(DisabledClubSearchService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IClubPostSearchService)
            && descriptor.ImplementationType == typeof(DisabledClubPostSearchService));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(ElasticsearchCircuitBreaker));
    }

    [Fact]
    public void AddApplicationServices_ShouldRegisterCoreServicesWithoutHostedServices_WhenDisabled()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        services.AddApplicationServices(config, includeHostedServices: false);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICaptchaService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(EventInvitationStatusConsumerOptions));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IClubInvitationService)
            && descriptor.ImplementationType == typeof(ClubInvitationService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IClubMemberInvitationService)
            && descriptor.ImplementationType == typeof(ClubMemberInvitationService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventWaitlistService)
            && descriptor.ImplementationType == typeof(EventWaitlistService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventWaitlistPromoter)
            && descriptor.ImplementationType == typeof(EventWaitlistPromoter));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventFavouriteService)
            && descriptor.ImplementationType == typeof(EventFavouriteService));
        // Not feature-gated: EventsService depends on it for private-event visibility.
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventAccessChecker)
            && descriptor.ImplementationType == typeof(EventAccessChecker));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddApplicationServices_ShouldRegisterDisabledFeatureServices_WhenFeatureFlagsAreOff()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureFlags:clubs.follow"] = "false",
                ["FeatureFlags:events.favourites"] = "false",
                ["FeatureFlags:events.invitations"] = "false",
                ["FeatureFlags:events.registration"] = "false",
                ["FeatureFlags:events.waitlist"] = "false",
                ["FeatureFlags:payment"] = "false",
                ["FeatureFlags:search.reindex"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        services.AddApplicationServices(config, includeHostedServices: true);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IFollowService)
            && descriptor.ImplementationType == typeof(DisabledFollowService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventInvitationService)
            && descriptor.ImplementationType == typeof(DisabledEventInvitationService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventRegistrationService)
            && descriptor.ImplementationType == typeof(DisabledEventRegistrationService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventWaitlistService)
            && descriptor.ImplementationType == typeof(DisabledEventWaitlistService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventWaitlistPromoter)
            && descriptor.ImplementationType == typeof(DisabledEventWaitlistPromoter));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventFavouriteService)
            && descriptor.ImplementationType == typeof(DisabledEventFavouriteService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IPaymentService)
            && descriptor.ImplementationType == typeof(DisabledPaymentService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventReindexService)
            && descriptor.ImplementationType == typeof(DisabledEventReindexService));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(EventInvitationStatusConsumerOptions));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(EventInvitationStatusConsumer));
    }

    [Fact]
    public void AddApplicationServices_ShouldResolveGoogleCaptcha_ByDefault()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplicationServices(config, includeHostedServices: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var captcha = scope.ServiceProvider.GetRequiredService<ICaptchaService>();

        captcha.Should().BeOfType<GoogleCaptchaService>();
    }

    [Fact]
    public void AddApplicationServices_ShouldResolveTurnstileCaptcha_WhenConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Captcha:Provider"] = "turnstile"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplicationServices(config, includeHostedServices: true);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var captcha = scope.ServiceProvider.GetRequiredService<ICaptchaService>();

        captcha.Should().BeOfType<CloudflareTurnstileCaptchaService>();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(EventInvitationStatusConsumer)).Should().BeTrue();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType?.Name == "ElasticsearchIndexInitializationService").Should().BeTrue();
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType?.Name == "ClubVersionCleanupService").Should().BeTrue();
    }

    private static string InvokeResolveCaptchaProvider(IConfiguration configuration)
    {
        var method = typeof(Container).GetMethod(
            "ResolveCaptchaProvider",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string)method.Invoke(null, [configuration])!;
    }
}
