using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Svrn7.Trust.Google;
using Xunit;

namespace HelloWorldConsole.Tests;

public class TelemetryTests
{
    [Fact]
    public void SourceAndMeterNamesAreStable()
    {
        // Consumers hard-code these in AddSource(...) / AddMeter(...).
        Assert.Equal("Svrn7.Trust.Google", GoogleTelemetry.ActivitySourceName);
        Assert.Equal("Svrn7.Trust.Google", GoogleTelemetry.MeterName);
    }

    [Fact]
    public async Task ResolveIdentity_NoIdToken_EmitsUserinfoSpan()
    {
        using var _ = ListenForSignOnActivities(out var activities);

        await GoogleIdentity.ResolveUserAsync(
            "client-id",
            "access-token",
            idToken: null,
            (_, _) => Task.FromResult(new GoogleUser("sub-1", "a@b.c", "Ada", true)),
            NullLogger.Instance,
            CancellationToken.None);

        // Other test classes emit on the same process-global source in parallel,
        // so assert our span is present rather than that it is the only one.
        Assert.Contains(activities, a =>
            a.OperationName == "GoogleSignOn.ResolveIdentity"
            && a.GetTagItem("signon.identity_source") is "userinfo"
            && a.Status != ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ResolveIdentity_InvalidIdToken_EmitsErrorSpan()
    {
        using var _ = ListenForSignOnActivities(out var activities);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GoogleIdentity.ResolveUserAsync(
                "client-id.apps.googleusercontent.com",
                "access-token",
                "not-a-valid.jwt",
                (_, _) => Task.FromResult(new GoogleUser("sub", "a@b.c", "n", true)),
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Contains(activities, a =>
            a.OperationName == "GoogleSignOn.ResolveIdentity"
            && a.GetTagItem("signon.identity_source") is "id_token"
            && a.Status == ActivityStatusCode.Error
            && a.GetTagItem("error.type") is "Google.Apis.Auth.InvalidJwtException");
    }

    private static ActivityListener ListenForSignOnActivities(out List<Activity> activities)
    {
        var collected = new List<Activity>();
        activities = collected;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GoogleTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (collected)
                {
                    collected.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
