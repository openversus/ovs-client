#pragma once
#ifndef OVS_NOTIFICATION_POLLER_H
#define OVS_NOTIFICATION_POLLER_H

#include <string>

namespace OVS::NotificationPoller {

    // Start the background polling thread. Polls the live server's
    // /ovs/notifications endpoint every 2 seconds and dispatches any
    // queued notifications. Safe to call once at startup; idempotent.
    //
    // serverUrl format: "http://192.168.1.10:8000" (no trailing slash, no path)
    void Start(const std::string& serverUrl);

    // Signal the poller thread to stop. Returns immediately; the thread
    // will exit on its next poll cycle.
    void Stop();

} // namespace OVS::NotificationPoller

#endif // OVS_NOTIFICATION_POLLER_H
