-- Виконувати лише після зупинки всіх екземплярів Watcher API.
-- Параметри передаються через psql: operation_id та actor_id.
\set ON_ERROR_STOP on

BEGIN;

WITH recovered AS (
    UPDATE ukrainealarm_webhook_operations AS operation
    SET "State" = 'Unknown',
        "OutcomeCode" = 'operator_recovered_after_shutdown',
        "UpdatedAtUtc" = CURRENT_TIMESTAMP
    WHERE operation."Id" = :'operation_id'::uuid
      AND operation."State" IN ('Pending', 'Running')
      AND EXISTS (
          SELECT 1
          FROM user_roles AS assignment
          JOIN roles AS role ON role."Id" = assignment."RoleId"
          JOIN users AS actor ON actor."Id" = assignment."UserId"
          WHERE assignment."UserId" = :'actor_id'::uuid
            AND assignment."LocationId" IS NULL
            AND role."Code" = 'admin'
            AND actor."IsActive"
      )
    RETURNING operation."Id", operation."Action", operation."IntegrationCode"
)
INSERT INTO audit_events ("Id", "EventType", "ActorType", "ActorId", "Payload", "OccurredAtUtc")
SELECT gen_random_uuid(),
       'integration.ukrainealarm_webhook.recovered_after_shutdown',
       'user',
       :'actor_id'::uuid,
       jsonb_build_object(
           'operationId', recovered."Id",
           'action', recovered."Action",
           'integrationCode', recovered."IntegrationCode",
           'outcome', 'operator_recovered_after_shutdown'
       ),
       CURRENT_TIMESTAMP
FROM recovered;

COMMIT;
