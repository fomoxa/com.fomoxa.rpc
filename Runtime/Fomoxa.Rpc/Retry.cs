using System;
using System.Collections.Generic;

namespace Fomoxa.Rpc
{
    public sealed class RetryPolicy
    {
        public int MaxAttempts { get; init; } = 3;

        public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(50);

        public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(2);

        public ISet<uint> Retryable { get; init; } =
            new HashSet<uint> { StatusCode.Unavailable, StatusCode.ResourceExhausted };

        public static RetryPolicy Never => new RetryPolicy
        {
            MaxAttempts = 1,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            Retryable = new HashSet<uint>(),
        };

        public static RetryPolicy Default => new RetryPolicy();

        public RetryPolicy WithAttempts(int maxAttempts) => new RetryPolicy
        {
            MaxAttempts = maxAttempts,
            InitialBackoff = InitialBackoff,
            MaxBackoff = MaxBackoff,
            Retryable = Retryable,
        };

        public RetryPolicy WithBackoff(TimeSpan initialBackoff) => new RetryPolicy
        {
            MaxAttempts = MaxAttempts,
            InitialBackoff = initialBackoff,
            MaxBackoff = MaxBackoff,
            Retryable = Retryable,
        };

        public TimeSpan BackoffFor(int attempt)
        {
            int doubling = Math.Min(Math.Max(attempt - 1, 0), 16);
            var scaled = TimeSpan.FromTicks(InitialBackoff.Ticks * (1L << doubling));
            return scaled < MaxBackoff ? scaled : MaxBackoff;
        }
    }

    public enum GiveUp
    {
        NotRetryable,
        UnsafeToRepeat,
        AttemptsExhausted,
        BudgetExhausted,
        StreamingCall,
    }

    public readonly struct RetryDecision
    {
        private RetryDecision(TimeSpan? retryAt, GiveUp reason)
        {
            RetryAt = retryAt;
            Reason = reason;
        }

        public TimeSpan? RetryAt { get; }

        public GiveUp Reason { get; }

        public bool ShouldRetry => RetryAt.HasValue;

        public static RetryDecision At(TimeSpan when) => new RetryDecision(when, default);

        public static RetryDecision Stop(GiveUp reason) => new RetryDecision(null, reason);
    }

    public sealed class RetryState
    {
        public RetryState(TimeSpan now, TimeSpan? budget)
        {
            Deadline = budget.HasValue ? now + budget.Value : null;
        }

        public int Attempt { get; private set; } = 1;

        public TimeSpan? Deadline { get; }

        public TimeSpan? Remaining(TimeSpan now)
        {
            if (!Deadline.HasValue)
            {
                return null;
            }
            var left = Deadline.Value - now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        public RetryDecision OnFailure(RetryPolicy policy, Status status, Method method, TimeSpan now)
        {
            if (method.Shape != Shape.Unary)
            {
                return RetryDecision.Stop(GiveUp.StreamingCall);
            }
            if (!policy.Retryable.Contains(status.Code))
            {
                return RetryDecision.Stop(GiveUp.NotRetryable);
            }
            if (status.Code == StatusCode.Unavailable && !method.Policy.Idempotent)
            {
                return RetryDecision.Stop(GiveUp.UnsafeToRepeat);
            }
            if (Attempt >= policy.MaxAttempts)
            {
                return RetryDecision.Stop(GiveUp.AttemptsExhausted);
            }
            var backoff = policy.BackoffFor(Attempt);
            if (Deadline.HasValue && now + backoff >= Deadline.Value)
            {
                return RetryDecision.Stop(GiveUp.BudgetExhausted);
            }
            Attempt++;
            return RetryDecision.At(now + backoff);
        }
    }
}
