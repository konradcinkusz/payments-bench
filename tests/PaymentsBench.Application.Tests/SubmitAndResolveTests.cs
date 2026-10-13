using PaymentsBench.Application.Payments;
using PaymentsBench.Application.Ports;
using PaymentsBench.Domain.Ledger;
using PaymentsBench.Domain.Payments;

namespace PaymentsBench.Application.Tests;

public class SubmitAndResolveTests
{
    [Fact]
    public async Task Accepted_submit_moves_to_accepted_with_the_partner_reference()
    {
        var host = new TestHost();
        var payment = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Accepted, "P-42", null));

        await host.Submit.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);

        var stored = await host.Payments.GetAsync(payment.Id, CancellationToken.None);
        Assert.Equal(PaymentStatus.Accepted, stored!.Status);
        Assert.Equal("P-42", stored.PartnerReference);
        Assert.Contains(host.Outbox.Events, e => e is PaymentSubmitted);
        Assert.Contains(host.Outbox.Events, e => e is PaymentAccepted);
    }

    [Fact]
    public async Task Rejected_submit_releases_the_funds()
    {
        var host = new TestHost();
        var payment = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Rejected, null, "AC01"));

        await host.Submit.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);

        var stored = await host.Payments.GetAsync(payment.Id, CancellationToken.None);
        Assert.Equal(PaymentStatus.Rejected, stored!.Status);
        Assert.Equal("AC01", stored.ReasonCode);
        Assert.Equal(0m, await host.Ledger.BalanceAsync(LedgerAccounts.PendingOutbound, "EUR", CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_after_submit_is_never_resubmitted_blind()
    {
        var host = new TestHost();
        var payment = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Unknown, null, "TaskCanceledException"));
        host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Accepted, "P-7", null, null));

        await host.Submit.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);
        var afterSubmit = await host.Payments.GetAsync(payment.Id, CancellationToken.None);
        Assert.Equal(PaymentStatus.Submitted, afterSubmit!.Status);

        var outcome = await host.Resolve.HandleAsync(new ResolveSubmittedPayment(payment.Id), CancellationToken.None);

        Assert.Equal(ResolutionOutcome.Accepted, outcome);
        Assert.Single(host.Partner.Submits);
        Assert.Equal(["E2E-1"], host.Partner.StatusQueries);
        var resolved = await host.Payments.GetAsync(payment.Id, CancellationToken.None);
        Assert.Equal(PaymentStatus.Accepted, resolved!.Status);
        Assert.Equal("P-7", resolved.PartnerReference);
    }

    [Fact]
    public async Task Resubmit_happens_only_when_the_partner_has_no_record()
    {
        var host = new TestHost();
        var payment = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Unknown, null, "HttpRequestException"));
        host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.NotFound, null, null, null));
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Accepted, "P-2", null));

        await host.Submit.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);
        var outcome = await host.Resolve.HandleAsync(new ResolveSubmittedPayment(payment.Id), CancellationToken.None);

        Assert.Equal(ResolutionOutcome.Accepted, outcome);
        Assert.Equal(2, host.Partner.Submits.Count);
        Assert.All(host.Partner.Submits, t => Assert.Equal("E2E-1", t.EndToEndId));
    }

    [Fact]
    public async Task A_resubmit_whose_own_outcome_is_unknown_is_still_unknown_and_the_payment_stays_submitted()
    {
        var host = new TestHost();
        var payment = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Unknown, null, "HttpRequestException"));
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Unknown, null, "TimeoutRejectedException"));
        await host.Submit.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);
        var commitsBefore = host.UnitOfWork.Commits;
        var eventsBefore = host.Outbox.Events.Count;

        var outcome = await host.Resolve.HandleAsync(new ResolveSubmittedPayment(payment.Id), CancellationToken.None);

        Assert.Equal(ResolutionOutcome.StillUnknown, outcome);
        Assert.Equal(PaymentStatus.Submitted, (await host.Payments.GetAsync(payment.Id, CancellationToken.None))!.Status);
        Assert.Equal(commitsBefore, host.UnitOfWork.Commits);
        Assert.Equal(eventsBefore, host.Outbox.Events.Count);

        // The next resolve asks the partner again before it does anything else (ADR 0001).
        host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Accepted, "P-9", null, null));
        var next = await host.Resolve.HandleAsync(new ResolveSubmittedPayment(payment.Id), CancellationToken.None);
        Assert.Equal(ResolutionOutcome.Accepted, next);
        Assert.Equal(2, host.Partner.Submits.Count);
    }

    [Fact]
    public async Task An_unavailable_partner_leaves_the_payment_submitted_and_is_never_a_resubmit()
    {
        var host = new TestHost();
        var payment = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);
        host.Partner.SubmitAnswers.Enqueue(new SubmitResult(SubmitOutcome.Unknown, null, "TimeoutRejectedException"));
        await host.Submit.HandleAsync(new SubmitPayment(payment.Id), CancellationToken.None);
        var commitsBefore = host.UnitOfWork.Commits;
        var eventsBefore = host.Outbox.Events.Count;
        host.Partner.StatusAnswers.Enqueue(new PartnerStatusResult(PartnerStatus.Unavailable, null, "BrokenCircuitException", null));

        var outcome = await host.Resolve.HandleAsync(new ResolveSubmittedPayment(payment.Id), CancellationToken.None);

        Assert.Equal(ResolutionOutcome.PartnerUnavailable, outcome);
        Assert.Equal(PaymentStatus.Submitted, (await host.Payments.GetAsync(payment.Id, CancellationToken.None))!.Status);
        Assert.Single(host.Partner.Submits);
        Assert.Equal(commitsBefore, host.UnitOfWork.Commits);
        Assert.Equal(eventsBefore, host.Outbox.Events.Count);
    }

    [Fact]
    public async Task Resolving_a_payment_that_is_not_submitted_does_nothing()
    {
        var host = new TestHost();
        var payment = await host.Initiate.HandleAsync(TestHost.Command(), CancellationToken.None);

        var outcome = await host.Resolve.HandleAsync(new ResolveSubmittedPayment(payment.Id), CancellationToken.None);

        Assert.Equal(ResolutionOutcome.AlreadyResolved, outcome);
        Assert.Empty(host.Partner.StatusQueries);
    }
}
