# Shadow review history mismatch: triage

Date: 5 October 2026. Inspected application version: 1.5.6.

## Classification

P2 / medium: review-history integrity and recovery gap.

The retained sufficient-evidence report is reproducibly absent from saved
history. The event that made the saved snapshot stale is not established.
Treat the origin of the stale snapshot as needs-info. Keep it separate from
the confirmed lack of recovery.

## Observed behavior

The retained reports contain these attempts:

| Date and time, UTC | Report result | Saved history |
| --- | --- | --- |
| 28 September, 08:43 | Sufficient evidence | Listed |
| 29 September, 06:43 | Truncated input | Not listed; failed attempts do not become completed history |
| 29 September, 12:43 | Sufficient evidence | Missing |

The truncated attempt reported a missing source CSV and an aggregate minute
cap warning. The later attempt started six hours after that failure, consistent
with the retry policy, and produced a complete report. The complete JSON and
Markdown files remain available. The app's report parser accepts the later
JSON. This does not prove that the scheduler accepted its completion.

The host `state.json` still contains only the 28 September result. Its
creation and modification timestamps also remain on 28 September. These
timestamps do not establish who last supplied the file or why it is stale.

The app's snapshot reader reproduces the missing entry from an isolated copy
of this state and its reports. An isolated scheduler restart preserves the
incomplete history. Host path resolution matches the installed app's store;
sandbox path redirection was excluded from the diagnosis.

## Confirmed mechanism and impact

The dashboard reads `state.json.History`. It does not scan the report directory.
The scheduler accepts a valid saved snapshot at startup. It does not compare
that snapshot with retained reports. A new successful review prepends its own
summary to the existing history, so it does not recover an older missing entry.

The scheduler also uses the snapshot's next-attempt time. The observed deadline
is 5 October at 08:43 UTC, or 16:43 in Malaysia. That deadline was still in the
future during triage. It is earlier than seven days after the later report,
not overdue. This mismatch does not establish telemetry loss or a change to
live alerts, accepted references, or monitoring behavior.

## Cause assessment

An old snapshot being restored is consistent with the evidence but is not
proven. The one retained `errors.log` contains no state-persistence error after
the 28 September result. No rotated error log was present in that directory.

A completion-write failure alone does not explain the untouched older state,
assuming the later reports came from this scheduler and store. The scheduler
must save a `RUNNING` snapshot before launching the child. If completion fails,
it attempts to save `FAILED` and logs the failure. The later report passes the
current parser and fits the bounded snapshot writer. A current host path
mismatch was excluded by direct checks. These checks do not establish every
condition of the historical completion attempt.

Do not label the historical cause as a confirmed failed write or installer
rollback without additional evidence.

## Recommended repair scope

Make accepted completions recoverable independently of the compact snapshot.
Write a durable completion record after the evaluator exits successfully and
the report passes validation, then update the snapshot. Bind the record to the
store identity, run ID, actual completion time, cutoff, report basename, and
report content hash.

A parseable report alone is not a completion record. The evaluator can write
JSON before its later work or process exit fails. The report filename contains
the start time, not the completion time. Existing unindexed reports should be
shown as retained reports with unverified completion, unless an explicit
recovery policy validates their acceptance. Do not silently reset the schedule
from a filename or filesystem timestamp.

Acceptance checks:

- Recover validated completions from stale, missing, or corrupt snapshots
  under the scheduler lock. Repeated recovery must not duplicate history.
- Reject mismatched or incomplete records. Truncated and invalid reports must
  not become successful completions.
- Preserve a newer active attempt, failure, and retry deadline during recovery.
- Use the recorded completion time to calculate the next weekly attempt.
- Test interruptions around report, completion-record, and snapshot writes.
  Persistence failures must remain visible and retryable.
- Preserve source recordings and full reports. Keep the existing history and
  snapshot size limits.

No production state was repaired during this triage.
