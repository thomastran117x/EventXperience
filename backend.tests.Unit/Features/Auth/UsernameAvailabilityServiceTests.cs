using backend.main.features.auth;
using backend.main.features.bloom;

using FluentAssertions;

using Moq;

namespace backend.tests.Unit.Features.Auth;

public class UsernameAvailabilityServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The only answer that permits skipping the database. A bloom filter has no false negatives,
    /// so a clear bit proves the name was never added.
    /// </summary>
    [Fact]
    public async Task IsUnavailableAsync_ShouldSkipTheDatabase_WhenTheFilterProvesTheNameIsFree()
    {
        var repository = new Mock<IAuthUserRepository>();
        var service = CreateService(repository, BloomFilterLookup.DefinitelyAbsent);

        var unavailable = await service.IsUnavailableAsync("ada", Now, AvailabilityLookupMode.Advisory);

        unavailable.Should().BeFalse();
        repository.Verify(
            r => r.UsernameUnavailableAsync(It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
    }

    /// <summary>
    /// The safety fix behind AvailabilityLookupMode. The local filter can lag a claim made on another
    /// instance, so a path that is about to take the name must confirm against the database —
    /// otherwise a stale "free" turns a clean 409 into a unique-index violation and a 500.
    /// </summary>
    [Fact]
    public async Task IsUnavailableAsync_ShouldQueryTheDatabase_EvenWhenTheFilterProvesAbsence()
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.UsernameUnavailableAsync("ada", Now)).ReturnsAsync(true);
        var service = CreateService(repository, BloomFilterLookup.DefinitelyAbsent);

        var unavailable = await service.IsUnavailableAsync("ada", Now, AvailabilityLookupMode.Authoritative);

        unavailable.Should().BeTrue();
        repository.Verify(r => r.UsernameUnavailableAsync("ada", Now), Times.Once);
    }

    [Fact]
    public async Task IsUnavailableAsync_ShouldDefaultToAuthoritative()
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.UsernameUnavailableAsync("ada", Now)).ReturnsAsync(true);
        var service = CreateService(repository, BloomFilterLookup.DefinitelyAbsent);

        // Callers must opt in to trusting the filter, never out of it.
        (await service.IsUnavailableAsync("ada", Now)).Should().BeTrue();
        repository.Verify(r => r.UsernameUnavailableAsync("ada", Now), Times.Once);
    }

    [Theory]
    [InlineData(BloomFilterLookup.PossiblyPresent)]
    [InlineData(BloomFilterLookup.Unavailable)]
    public async Task IsUnavailableAsync_ShouldConsultTheDatabase_ForEveryOtherAnswer(
        BloomFilterLookup lookup)
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.UsernameUnavailableAsync("ada", Now)).ReturnsAsync(true);
        var service = CreateService(repository, lookup);

        var unavailable = await service.IsUnavailableAsync("ada", Now);

        unavailable.Should().BeTrue();
        repository.Verify(r => r.UsernameUnavailableAsync("ada", Now), Times.Once);
    }

    /// <summary>
    /// A filter can be poisoned or merely collide, so a "present" answer is never authoritative —
    /// the database decides, and it can still say the name is free.
    /// </summary>
    [Fact]
    public async Task IsUnavailableAsync_ShouldLetTheDatabaseOverrideAFalsePositive()
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.UsernameUnavailableAsync("ada", Now)).ReturnsAsync(false);
        var service = CreateService(repository, BloomFilterLookup.PossiblyPresent);

        (await service.IsUnavailableAsync("ada", Now)).Should().BeFalse();
    }

    [Fact]
    public async Task IsUnavailableAsync_ShouldFallBackToTheDatabase_WhenTheFeatureIsDisabled()
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.UsernameUnavailableAsync("ada", Now)).ReturnsAsync(true);
        var service = new UsernameAvailabilityService(repository.Object, new DisabledBloomFilterRegistry());

        (await service.IsUnavailableAsync("ada", Now)).Should().BeTrue();
    }

    [Fact]
    public async Task IsUnavailableAsync_ShouldObserveCancellation()
    {
        var service = CreateService(new Mock<IAuthUserRepository>(), BloomFilterLookup.DefinitelyAbsent);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => service.IsUnavailableAsync("ada", Now, AvailabilityLookupMode.Advisory, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MarkTakenAsync_ShouldRecordTheUsername()
    {
        var bloom = new Mock<IBloomFilterRegistry>();
        var service = new UsernameAvailabilityService(Mock.Of<IAuthUserRepository>(), bloom.Object);

        await service.MarkTakenAsync("ada");

        bloom.Verify(
            b => b.AddAsync(BloomFilterTargets.Username, "ada", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task MarkTakenAsync_ShouldIgnoreAMissingUsername(string? username)
    {
        var bloom = new Mock<IBloomFilterRegistry>();
        var service = new UsernameAvailabilityService(Mock.Of<IAuthUserRepository>(), bloom.Object);

        await service.MarkTakenAsync(username!);

        bloom.Verify(
            b => b.AddAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Callers invoke this after the claiming write has committed, and AuthService turns any
    /// non-AppException into a 500 — so a filter failure here would report a successful signup as
    /// a server error. A missed bit is recoverable; a failed signup is not.
    /// </summary>
    [Fact]
    public async Task MarkTakenAsync_ShouldSwallowFilterFailures()
    {
        var bloom = new Mock<IBloomFilterRegistry>();
        bloom.Setup(b => b.AddAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis is unreachable"));
        var service = new UsernameAvailabilityService(Mock.Of<IAuthUserRepository>(), bloom.Object);

        var act = () => service.MarkTakenAsync("ada");

        await act.Should().NotThrowAsync();
    }

    private static UsernameAvailabilityService CreateService(
        Mock<IAuthUserRepository> repository,
        BloomFilterLookup lookup)
    {
        var bloom = new Mock<IBloomFilterRegistry>();
        bloom.Setup(b => b.MightContain(BloomFilterTargets.Username, It.IsAny<string>()))
            .Returns(lookup);

        return new UsernameAvailabilityService(repository.Object, bloom.Object);
    }
}
