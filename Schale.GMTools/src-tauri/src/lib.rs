mod manager;

use manager::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(Manager::default())
        .setup(|app| {
            manager::spawn_watcher(app.handle().clone());
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            manager::manager_status,
            manager::manager_check,
            manager::manager_update,
            manager::manager_start,
            manager::manager_stop,
            manager::manager_restart,
            manager::manager_logs,
            manager::manager_clear_logs,
            manager::manager_save_settings,
            manager::manager_pick_folder,
            manager::manager_open,
            manager::manager_play,
            manager::api_request,
        ])
        .build(tauri::generate_context!())
        .expect("error while building Schale GM Tools")
        .run(|app_handle, event| {
            if let tauri::RunEvent::Exit = event {
                manager::cleanup_on_exit(app_handle);
            }
        });
}
