using backend.main.features.auth;
using backend.main.features.bloom;

using FluentAssertions;

using Moq;

namespace backend.tests.Unit.Features.Auth;

public class EmailAvailabilityServiceTests
{
    /// <summary>
    /// The only answer that permits skipping the database. A bloom filter has no false negatives,
    /// so a clear bit proves the address was never added.
    /// </summary>
    [Fact]
    public async Task IsRegisteredAsync_ShouldSkipTheDatabase_WhenTheFilterProvesTheAddressIsUnknown()
    {
        var repository = new Mock<IAuthUserRepository>();
        var service = CreateService(repository, BloomFilterLookup.DefinitelyAbsent);

        var registered = await service.IsRegisteredAsync("ada@example.com", AvailabilityLookupMode.Advisory);

        registered.Should().BeFalse();
        repository.Verify(r => r.EmailExistsAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// The safety property behind AvailabilityLookupMode. The local filter can lag a signup made
    /// on another instance, so a path about to insert the account must confirm against the
    /// database — otherwise a stale "unknown" turns a clean 409 into a unique-index 500.
    /// </summary>
    [Fact]
    public async Task IsRegisteredAsync_ShouldQueryTheDatabase_EvenWhenTheFilterProvesAbsence()
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.EmailExistsAsync("ada@example.com")).ReturnsAsync(true);
        var service = CreateService(repository, BloomFilterLookup.DefinitelyAbsent);

        var registered = await service.IsRegisteredAsync("ada@example.com", AvailabilityLookupMode.Authoritative);

        registered.Should().BeTrue();
        repository.Verify(r => r.EmailExistsAsync("ada@example.com"), Times.Once);
    }

    [Fact]
    public async Task IsRegisteredAsync_ShouldDefaultToAuthoritative()
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.EmailExistsAsync("ada@example.com")).ReturnsAsync(true);
        var service = CreateService(repository, BloomFilterLookup.DefinitelyAbsent);

        // Callers must opt in to trusting the filter, never out of it.
        (await service.IsRegisteredAsync("ada@example.com")).Should().BeTrue();
        repository.Verify(r => r.EmailExistsAsync("ada@example.com"), Times.Once);
    }

    [Theory]
    [InlineData(BloomFilterLookup.PossiblyPresent)]
    [InlineData(BloomFilterLookup.Unavailable)]
    public async Task IsRegisteredAsync_ShouldConsultTheDatabase_ForEveryOtherAnswer(
        BloomFilterLookup lookup)
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.EmailExistsAsync("ada@example.com")).ReturnsAsync(true);
        var service = CreateService(repository, lookup);

        var registered = await service.IsRegisteredAsync("ada@example.com", AvailabilityLookupMode.Advisory);

        registered.Should().BeTrue();
        repository.Verify(r => r.EmailExistsAsync("ada@example.com"), Times.Once);
    }

    /// <summary>
    /// A filter can collide, so a "present" answer is never authoritative — the database decides,
    /// and it can still say the address is free.
    /// </summary>
    [Fact]
    public async Task IsRegisteredAsync_ShouldLetTheDatabaseOverrideAFalsePositive()
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.EmailExistsAsync("ada@example.com")).ReturnsAsync(false);
        var service = CreateService(repository, BloomFilterLookup.PossiblyPresent);

        (await service.IsRegisteredAsync("ada@example.com", AvailabilityLookupMode.Advisory))
            .Should().BeFalse();
    }

    [Fact]
    public async Task IsRegisteredAsync_ShouldFallBackToTheDatabase_WhenTheFeatureIsDisabled()
    {
        var repository = new Mock<IAuthUserRepository>();
        repository.Setup(r => r.EmailExistsAsync("ada@example.com")).ReturnsAsync(true);
        var service = new EmailAvailabilityService(repository.Object, new DisabledBloomFilterRegistry());

        (await service.IsRegisteredAsync("ada@example.com", AvailabilityLookupMode.Advisory))
            .Should().BeTrue();
    }

    [Fact]
    public async Task IsRegisteredAsync_ShouldObserveCancellation()
    {
        var service = CreateService(new Mock<IAuthUserRepository>(), BloomFilterLookup.DefinitelyAbsent);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => service.IsRegisteredAsync(
            "ada@example.com",
            AvailabilityLookupMode.Advisory,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void IsDefinitelyUnregistered_ShouldBeTrue_OnlyWhenTheFilterProvesAbsence()
    {
        var service = CreateService(new Mock<IAuthUserRepository>(), BloomFilterLookup.DefinitelyAbsent);

        service.IsDefinitelyUnregistered("ada@example.com").Should().BeTrue();
    }

    /// <summary>
    /// The whole point of this member is that it never queries: callers use it to skip a lookup
    /// they are about to make anyway, so an unsure filter must leave them doing exactly one.
    /// </summary>
    [Theory]
    [InlineData(BloomFilterLookup.PossiblyPresent)]
    [InlineData(BloomFilterLookup.Unavailable)]
    public void IsDefinitelyUnregistered_ShouldBeFalse_AndNeverQuery_WhenTheFilterIsUnsure(
        BloomFilterLookup lookup)
    {
        var repository = new Mock<IAuthUserRepository>();
        var service = CreateService(repository, lookup);

        service.IsDefinitelyUnregistered("ada@example.com").Should().BeFalse();
        repository.Verify(r => r.EmailExistsAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void IsDefinitelyUnregistered_ShouldBeFalse_ForAMissingAddress(string? email)
    {
        var service = CreateService(new Mock<IAuthUserRepository>(), BloomFilterLookup.DefinitelyAbsent);

        service.IsDefinitelyUnregistered(email!).Should().BeFalse();
    }

    [Fact]
    public async Task MarkRegisteredAsync_ShouldRecordTheAddress()
    {
        var bloom = new Mock<IBloomFilterRegistry>();
        var service = new EmailAvailabilityService(Mock.Of<IAuthUserRepository>(), bloom.Object);

        await service.MarkRegisteredAsync("ada@example.com");

        bloom.Verify(
            b => b.AddAsync(BloomFilterTargets.Email, "ada@example.com", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Target separation is what lets one registry back both filters: the target name is mixed
    /// into the hash, so an address must never be written under the username target.
    /// </summary>
    [Fact]
    public async Task MarkRegisteredAsync_ShouldNotWriteToTheUsernameFilter()
    {
        var bloom = new Mock<IBloomFilterRegistry>();
        var service = new EmailAvailabilityService(Mock.Of<IAuthUserRepository>(), bloom.Object);

        await service.MarkRegisteredAsync("ada@example.com");

        bloom.Verify(
            b => b.AddAsync(BloomFilterTargets.Username, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task MarkRegisteredAsync_ShouldIgnoreAMissingAddress(string? email)
    {
        var bloom = new Mock<IBloomFilterRegistry>();
        var service = new EmailAvailabilityService(Mock.Of<IAuthUserRepository>(), bloom.Object);

        await service.MarkRegisteredAsync(email!);

        bloom.Verify(
            b => b.AddAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Callers invoke this after the account row has committed, and AuthService turns any
    /// non-AppException into a 500 — so a filter failure here would report a successful signup as
    /// a server error. A missed bit is recoverable by the next rebuild; a failed signup is not.
    /// </summary>
    [Fact]
    public async Task MarkRegisteredAsync_ShouldSwallowFilterFailures()
    {
        var bloom = new Mock<IBloomFilterRegistry>();
        bloom.Setup(b => b.AddAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis is unreachable"));
        var service = new EmailAvailabilityService(Mock.Of<IAuthUserRepository>(), bloom.Object);

        var act = () => service.MarkRegisteredAsync("ada@example.com");

        await act.Should().NotThrowAsync();
    }

    private static EmailAvailabilityService CreateService(
        Mock<IAuthUserRepository> repository,
        BloomFilterLookup lookup)
    {
        var bloom = new Mock<IBloomFilterRegistry>();
        bloom.Setup(b => b.MightContain(BloomFilterTargets.Email, It.IsAny<string>()))
            .Returns(lookup);

        return new EmailAvailabilityService(repository.Object, bloom.Object);
    }
}
