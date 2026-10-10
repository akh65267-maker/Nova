# RabbitMQ with the Prometheus and shovel plugins enabled, for Azure Container Apps.
# (Shovel is what deploy/scripts/replay-errors.sh uses to replay _error queues.)
#
# docker-compose does this by bind-mounting deploy/rabbitmq/enabled_plugins.
# Container Apps has no bind mounts, so the plugin is enabled at build time.
# Build context: repository root.
FROM rabbitmq:3-management

RUN rabbitmq-plugins enable --offline rabbitmq_prometheus rabbitmq_shovel rabbitmq_shovel_management
