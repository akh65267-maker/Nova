using Xunit;

// Every test class here starts its own Postgres (and some a RabbitMQ) in Docker. xUnit runs
// different classes at the same time by default, so on a small CI runner several containers and
// several hosts compete for memory and disk, and a test can lose its database connection
// ("Exception while reading from stream") for reasons that have nothing to do with the code under
// test. The suite is dominated by the one slow saga class, whose tests already run one after the
// other, so running the classes one after the other as well costs about a minute and removes that
// pressure. (Added after exactly that failure; whether the pressure was the cause is a judgement,
// not something that was proven.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
