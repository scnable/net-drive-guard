use std::time::Duration;

use net_drive_guard_core::{
    config::Config,
    mapping::{plan, Mapping, Plan},
    monitor::{Action, NotificationGate, Observation},
    runtime::{Completion, Monitor},
    schedule::Trigger,
};

fn main() {
    println!("SIMULATION ONLY — no network, mapping, credential or startup operations.");
    let config = Config::new("Z:", r"\\demo-server\demo-share")
        .expect("built-in demo configuration")
        .with_automatic_operations(true, true);
    let mut monitor = Monitor::new(Some(config.clone()), Duration::ZERO);
    let mut notifications = NotificationGate::new(Duration::from_secs(30));
    let mut mapping = Mapping::Connected(config.share().to_owned());
    for (index, observation) in [
        Observation::Healthy,
        Observation::ServerUnreachable,
        Observation::ServerUnreachable,
        Observation::ServerUnreachable,
        Observation::ServerUnreachable,
        Observation::MappingMissing,
        Observation::Healthy,
    ]
    .into_iter()
    .enumerate()
    {
        let now = Duration::from_secs(index as u64 * 5);
        let request = monitor
            .start(Trigger::Manual, now)
            .expect("sequential demo checks");
        let Completion::Decision(decision) = monitor
            .finish(request.ticket, observation, request.checks, now)
            .expect("matching demo completion")
        else {
            unreachable!()
        };
        let operation = plan(&config, decision.action, &mapping);
        println!(
            "[SIMULATED] t={:2}s state={:?} requested={:?} plan={:?} notification={}",
            now.as_secs(),
            decision.snapshot.state,
            decision.action,
            operation,
            notifications.register(&decision, now)
        );
        // This is an in-memory fixture, not a system adapter or an execution result.
        match operation {
            Plan::Disconnect => mapping = Mapping::Missing,
            Plan::Connect | Plan::Replace => {
                mapping = Mapping::Connected(config.share().to_owned())
            }
            _ => {}
        }
        if decision.action == Action::RepairMapping {
            println!("[SIMULATED] repair requested; health still awaits the next observation.");
        }
    }
}
