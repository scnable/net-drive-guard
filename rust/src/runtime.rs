//! Single-owner, event-driven check lifecycle. The host runs read-only probes
//! and returns results. No worker threads are spawned or forcefully cancelled.

use std::time::Duration;

use crate::{
    config::Config,
    monitor::{evaluate, Action, Decision, Failure, Observation, Snapshot, State},
    schedule::{Checks, Schedule, Trigger},
};

/// Tickets cannot be manufactured by adapters. A stale completion must not
/// release a newer request or produce actions against newly saved settings.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Ticket(u64);

#[derive(Clone, Debug)]
pub struct Request {
    pub ticket: Ticket,
    pub checks: Checks,
    pub config: Config,
    pub started_at: Duration,
    pub deadline: Duration,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StartError {
    Busy,
    Paused,
    NotConfigured,
    NotDue,
    ClockOverflow,
    TicketExhausted,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FinishError {
    StaleTicket,
    UnrequestedLayer,
    ClockReversed,
    InsufficientEvidence,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Completion {
    Decision(Decision),
    Discarded,
}

struct Pending {
    request: Request,
    discarded: bool,
}

/// Own from one event loop (or protect with a mutex). All mutations require
/// `&mut self`; the request slot stays occupied until the adapter acknowledges
/// completion, including after timeout or pause. A stuck adapter cannot cause
/// unbounded replacement workers or overlapping checks.
pub struct Monitor {
    config: Option<Config>,
    snapshot: Snapshot,
    schedule: Schedule,
    pending: Option<Pending>,
    next_ticket: u64,
    paused: bool,
}

impl Monitor {
    pub fn new(config: Option<Config>, now: Duration) -> Self {
        Self {
            config,
            snapshot: Snapshot::initial(now),
            schedule: Schedule::default(),
            pending: None,
            next_ticket: 0,
            paused: false,
        }
    }

    pub fn snapshot(&self) -> Snapshot {
        self.snapshot
    }
    pub fn schedule(&self) -> Schedule {
        self.schedule
    }
    pub fn config(&self) -> Option<&Config> {
        self.config.as_ref()
    }
    pub fn is_busy(&self) -> bool {
        self.pending.is_some()
    }

    /// Save settings only after an outstanding probe has returned. Reset old
    /// observations so an old server's failures cannot remove a new mapping.
    /// Saving while paused preserves pause; resume will request a fresh check.
    pub fn configure(&mut self, config: Config, now: Duration) -> Result<(), StartError> {
        if self.is_busy() {
            return Err(StartError::Busy);
        }
        self.config = Some(config);
        self.schedule = Schedule::default();
        self.snapshot = Snapshot::initial(now);
        if self.paused {
            self.snapshot.state = State::Paused;
        }
        Ok(())
    }

    pub fn pause(&mut self, now: Duration) {
        self.paused = true;
        self.snapshot.state = State::Paused;
        self.snapshot.transitioned_at = now;
        self.snapshot.consecutive_failures = 0;
        if let Some(pending) = self.pending.as_mut() {
            pending.discarded = true;
        }
    }

    pub fn resume(&mut self, now: Duration) {
        self.paused = false;
        self.snapshot = Snapshot::initial(now);
        self.schedule = Schedule::default();
    }

    pub fn start(&mut self, trigger: Trigger, now: Duration) -> Result<Request, StartError> {
        if self.is_busy() {
            return Err(StartError::Busy);
        }
        if self.paused {
            return Err(StartError::Paused);
        }
        let config = self.config.as_ref().ok_or(StartError::NotConfigured)?;
        let checks = self
            .schedule
            .due(Some(config), self.snapshot.state, trigger, now);
        if checks == Checks::NONE {
            return Err(StartError::NotDue);
        }
        let deadline = now
            .checked_add(config.overall_timeout())
            .ok_or(StartError::ClockOverflow)?;
        let next_ticket = self
            .next_ticket
            .checked_add(1)
            .ok_or(StartError::TicketExhausted)?;
        let request = Request {
            ticket: Ticket(self.next_ticket),
            checks,
            config: config.clone(),
            started_at: now,
            deadline,
        };
        self.next_ticket = next_ticket;
        self.pending = Some(Pending {
            request: request.clone(),
            discarded: false,
        });
        Ok(request)
    }

    /// Call from the host timer. Timeout produces a failure but keeps the slot
    /// occupied until the adapter returns. The host should request cooperative
    /// cancellation; this core cannot interrupt a blocking Windows API call.
    pub fn expire(&mut self, now: Duration) -> Option<Decision> {
        let pending = self.pending.as_mut()?;
        if pending.discarded || now < pending.request.deadline {
            return None;
        }
        pending.discarded = true;
        let decision = evaluate(
            &pending.request.config,
            self.snapshot,
            Observation::Failed {
                reason: Failure::Timeout,
                native_code: None,
            },
            now,
        );
        self.snapshot = decision.snapshot;
        Some(decision)
    }

    pub fn finish(
        &mut self,
        ticket: Ticket,
        observation: Observation,
        completed: Checks,
        now: Duration,
    ) -> Result<Completion, FinishError> {
        let pending = self.pending.as_ref().ok_or(FinishError::StaleTicket)?;
        if pending.request.ticket != ticket {
            return Err(FinishError::StaleTicket);
        }
        if now < pending.request.started_at {
            return Err(FinishError::ClockReversed);
        }
        let requested = pending.request.checks;
        if (completed.mapping && !requested.mapping)
            || (completed.tcp && !requested.tcp)
            || (completed.share && !requested.share)
        {
            return Err(FinishError::UnrequestedLayer);
        }
        if let Some(timeout) = self.expire(now) {
            self.pending = None;
            return Ok(Completion::Decision(timeout));
        }
        // A late/cancelled callback only acknowledges the old worker; it must
        // not be validated as fresh evidence or produce a new action.
        if self
            .pending
            .as_ref()
            .is_some_and(|pending| pending.discarded)
        {
            self.pending = None;
            return Ok(Completion::Discarded);
        }
        let enough_evidence = match observation {
            Observation::Healthy => completed.mapping && completed.share,
            Observation::ServerUnreachable => completed.tcp,
            Observation::MappingMissing => completed.mapping && completed.tcp,
            _ => true,
        };
        if !enough_evidence {
            return Err(FinishError::InsufficientEvidence);
        }
        let pending = self.pending.take().expect("ticket was validated above");
        self.schedule.completed(completed, now);
        let decision = evaluate(&pending.request.config, self.snapshot, observation, now);
        self.snapshot = decision.snapshot;
        Ok(Completion::Decision(decision))
    }

    pub fn operation_started(&mut self, now: Duration) -> Result<(), StartError> {
        if self.is_busy() {
            return Err(StartError::Busy);
        }
        if self.paused {
            return Err(StartError::Paused);
        }
        let config = self.config.as_ref().ok_or(StartError::NotConfigured)?;
        self.snapshot = evaluate(config, self.snapshot, Observation::Repairing, now).snapshot;
        Ok(())
    }

    /// Feed an actual operation failure back before the next observation. A
    /// requested action or a successful TCP probe is never promoted to Healthy.
    /// The host must serialize remediation with probes and configuration saves.
    pub fn operation_failed(
        &mut self,
        reason: Failure,
        native_code: Option<u32>,
        now: Duration,
    ) -> Result<Decision, StartError> {
        if self.is_busy() {
            return Err(StartError::Busy);
        }
        if self.paused {
            return Err(StartError::Paused);
        }
        let config = self.config.as_ref().ok_or(StartError::NotConfigured)?;
        let decision = evaluate(
            config,
            self.snapshot,
            Observation::Failed {
                reason,
                native_code,
            },
            now,
        );
        debug_assert_eq!(decision.action, Action::None);
        self.snapshot = decision.snapshot;
        Ok(decision)
    }
}
