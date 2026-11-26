# DOK-ARCH-006 — Infrastructure dependency ban

Status: implemented (`dokimos architecture check`; requirement RQ-DOKIMOS-ARCH-001, INIT-012 infrastructure
minimalism).

## Activation

Active only when `.echelon/reliability.json` declares `continuousAvailability` `applicable` or
`partially-applicable`; otherwise `not-applicable` (never a pass). An undecodable declaration is `unavailable`.

## Rule

No MSBuild project file may reference a message-broker or distributed-lock client package through
`<PackageReference>` or `<GlobalPackageReference>`, unless a valid exception accepts that exact dependency.

Banned (`ReliabilityArchitecture.infrastructureClients`; family = id and `<id>.*`, case-insensitive):

| Category | Packages |
|---|---|
| Kafka | `Confluent.Kafka`, `Confluent.SchemaRegistry`, `MassTransit.Kafka` (family) |
| RabbitMQ / AMQP | `RabbitMQ.Client`, `RabbitMQ.Stream.Client`, `EasyNetQ`, `MassTransit.RabbitMQ`, `NServiceBus.RabbitMQ`, `NServiceBus.Transport.RabbitMQ`, `Rebus.RabbitMq`, `Apache.NMS.AMQP` (family) |
| ActiveMQ | `MassTransit.ActiveMQ`, `Apache.NMS.ActiveMQ` (family) |
| NATS | `NATS.Client`, `NATS.Net` (family) |
| Managed brokers | `Azure.Messaging.ServiceBus`, `Microsoft.Azure.ServiceBus`, `MassTransit.Azure.ServiceBus.Core`, `Azure.Messaging.EventHubs`, `MassTransit.AmazonSQS`, `Google.Cloud.PubSub.V1` (family); `AWSSDK.SQS` (exact) |
| Distributed locks | `RedLock.net` (family); `DistributedLock`, `DistributedLock.Redis`, `.ZooKeeper`, `.Azure`, `.SqlServer`, `.Postgres`, `.MySql`, `.Oracle`, `.MongoDB` (exact) |

Not banned: `DistributedLock.Core` (abstractions), `DistributedLock.FileSystem` and `DistributedLock.WaitHandles`
(single-machine locks), and database drivers such as `Npgsql`. Redis clients are DOK-ARCH-005.

## Finding, scope and exceptions

As DOK-ARCH-005: scope `<project file>#<PackageId>`; exit 4 on a violation; exceptions in
`quality/exceptions.json` with `ruleId: "DOK-ARCH-006"` must name a `DF-`/`ADR-` decision record in `evidence`.

## What a pass does not establish

Static evidence only: no claim about delivery guarantees, idempotency or availability at runtime. Transitive and
framework references, paket, property-valued includes and non-.NET ecosystems are not analyzed (no MSBuild
project file makes the rule `unavailable`). A named decision record is not proof of its acceptance.
