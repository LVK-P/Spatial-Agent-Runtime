using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// EventBus unit tests — subscription isolation, wildcard handling,
    /// thread safety, and failure containment.
    /// </summary>
    public class EventBusTests
    {
        private static AgentEvent Evt(string type) => new() { EventType = type, Turn = 1 };

        [Fact]
        public void Subscriber_receives_matching_event()
        {
            var bus = new EventBus();
            AgentEvent? received = null;
            bus.Subscribe("tool.started", e => received = e);

            bus.Publish(Evt("tool.started"));

            received.Should().NotBeNull();
            received!.EventType.Should().Be("tool.started");
        }

        [Fact]
        public void Subscriber_does_not_receive_non_matching_event()
        {
            var bus = new EventBus();
            AgentEvent? received = null;
            bus.Subscribe("tool.started", e => received = e);

            bus.Publish(Evt("tool.completed"));

            received.Should().BeNull();
        }

        [Fact]
        public void Wildcard_subscriber_receives_all_events()
        {
            var bus = new EventBus();
            var received = new List<string>();
            bus.Subscribe("*", e => received.Add(e.EventType));

            bus.Publish(Evt("tool.started"));
            bus.Publish(Evt("tool.completed"));
            bus.Publish(Evt("safety.blocked"));

            received.Should().Contain("tool.started");
            received.Should().Contain("tool.completed");
            received.Should().Contain("safety.blocked");
        }

        [Fact]
        public void Multiple_subscribers_all_receive_event()
        {
            var bus = new EventBus();
            int count = 0;
            bus.Subscribe("turn.start", _ => count++);
            bus.Subscribe("turn.start", _ => count++);
            bus.Subscribe("turn.start", _ => count++);

            bus.Publish(Evt("turn.start"));

            count.Should().Be(3);
        }

        [Fact]
        public void Failing_subscriber_does_not_block_other_subscribers()
        {
            var bus = new EventBus();
            int secondCalled = 0;
            bus.Subscribe("tool.started", _ => throw new InvalidOperationException("boom"));
            bus.Subscribe("tool.started", _ => secondCalled++);

            var act = () => bus.Publish(Evt("tool.started"));

            act.Should().NotThrow();
            secondCalled.Should().Be(1, "second subscriber must still execute after first throws");
        }

        [Fact]
        public void Unsubscribed_handler_no_longer_receives_events()
        {
            var bus = new EventBus();
            int count = 0;
            var sub = bus.Subscribe("tool.started", _ => count++);

            bus.Publish(Evt("tool.started"));
            count.Should().Be(1);

            sub.Dispose();
            bus.Publish(Evt("tool.started"));
            count.Should().Be(1, "no additional calls after unsubscribe");
        }

        [Fact]
        public void Concurrent_publishes_do_not_throw()
        {
            var bus = new EventBus();
            bus.Subscribe("*", _ => { }); // ensure handlers list non-empty

            var tasks = Enumerable.Range(0, 20).Select(i =>
                Task.Run(() => bus.Publish(Evt($"event_{i}"))));

            var act = () => Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));

            act.Should().NotThrow();
        }
    }
}
