/*
 * EBMC Stop: large red button under the Temperature tab.
 * One confirmation dialog, then POST {"command": "stop"} to /api/plugin/ebmc_stop.
 */
$(function () {
    function EbmcStopViewModel(parameters) {
        var self = this;

        self.loginState = parameters[0];
        self.access = parameters[1];
        self.printerState = parameters[2];

        self.busy = ko.observable(false);

        self.hasPermission = ko.pureComputed(function () {
            return self.loginState.hasPermission(self.access.permissions.PRINT);
        });

        self.canStop = ko.pureComputed(function () {
            return (
                self.hasPermission() && self.printerState.isOperational() && !self.busy()
            );
        });

        self.disabledReason = ko.pureComputed(function () {
            if (!self.hasPermission()) {
                return "You need to be logged in with print permission to use this button.";
            }
            if (!self.printerState.isOperational()) {
                return "The printer is not connected.";
            }
            if (self.busy()) {
                return "Stopping...";
            }
            return "";
        });

        self.confirmStop = function () {
            if (!self.canStop()) return;

            var printing =
                self.printerState.isPrinting() || self.printerState.isPaused();
            var message = printing
                ? "This will CANCEL the current print. When the printer has stopped it will be homed."
                : "No print is running. The printer will be homed.";

            showConfirmationDialog({
                title: "Stop print and home all axes?",
                message: message,
                question: printing
                    ? "The print cannot be resumed afterwards. Continue?"
                    : "Continue?",
                cancel: printing ? "No, keep printing" : "No",
                proceed: "Yes, STOP and HOME",
                proceedClass: "danger",
                onproceed: self.sendStop
            });
        };

        self.progressNotice = undefined;

        self.closeProgress = function () {
            if (self.progressNotice) {
                self.progressNotice.remove();
                self.progressNotice = undefined;
            }
        };

        // True from the request until the server's result message arrives. The
        // result can arrive (over the socket) before the HTTP response does.
        self.awaitingResult = false;

        self.sendStop = function () {
            self.busy(true);
            self.awaitingResult = true;
            OctoPrint.simpleApiCommand("ebmc_stop", "stop", {})
                .done(function (response) {
                    if (!self.awaitingResult) return;
                    self.closeProgress();
                    self.progressNotice = new PNotify({
                        title: "Stopping...",
                        text:
                            response && response.cancelling
                                ? "Waiting for the print to stop. The printer is NOT homed yet."
                                : "Waiting for the printer to be idle. The printer is NOT homed yet.",
                        type: "info",
                        hide: false
                    });
                })
                .fail(function (jqXHR) {
                    self.awaitingResult = false;
                    var text = "The printer did not accept the stop request.";
                    if (jqXHR && jqXHR.responseJSON && jqXHR.responseJSON.error) {
                        text = jqXHR.responseJSON.error;
                    }
                    new PNotify({title: "Stop and home refused", text: text, type: "error"});
                })
                .always(function () {
                    self.busy(false);
                });
        };

        // Sent by the server once the G-code has actually been sent, or on failure.
        self.onDataUpdaterPluginMessage = function (plugin, data) {
            if (plugin !== "ebmc_stop" || !data) return;
            self.awaitingResult = false;
            self.closeProgress();
            if (data.type === "sent") {
                new PNotify({title: "Homing", text: data.text, type: "success"});
            } else if (data.type === "error") {
                new PNotify({
                    title: "Stop and home FAILED",
                    text: data.text,
                    type: "error",
                    hide: false
                });
            }
        };

        // Move the button into the Temperature tab after all view models are
        // bound, so the temperature view model never binds our markup.
        self.onAllBound = function () {
            $("#ebmc_stop_panel").appendTo("#temp");
        };
    }

    OCTOPRINT_VIEWMODELS.push({
        construct: EbmcStopViewModel,
        dependencies: ["loginStateViewModel", "accessViewModel", "printerStateViewModel"],
        elements: ["#ebmc_stop_panel"]
    });
});
