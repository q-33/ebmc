/*
 * EBMC nozzle height: a small panel in the Control tab.
 * Read: POST {"command": "nozzle_height"}. Change: read first, confirm in words and
 * numbers, then POST {"command": "nozzle_adjust", direction, step_mm, expected_before,
 * expected_target}.
 * The server reports progress and the result as plugin messages.
 */
$(function () {
    function EbmcNozzleViewModel(parameters) {
        var self = this;

        self.loginState = parameters[0];
        self.access = parameters[1];
        self.printerState = parameters[2];

        self.busy = ko.observable(false);
        self.stored = ko.observable(undefined);
        self.info = ko.observable(undefined); // last read response
        self.steps = ko.observableArray([0.02, 0.05, 0.1]);
        self.step = ko.observable(0.05);

        self.fmt = function (v) {
            return Number(v).toFixed(2);
        };
        self.stepLabel = function (s) {
            return s + " mm";
        };

        self.hasPermission = ko.pureComputed(function () {
            return self.loginState.hasPermission(self.access.permissions.CONTROL);
        });

        self.disabledReason = ko.pureComputed(function () {
            if (!self.hasPermission()) {
                return "You need OctoPrint's Control permission to read or change the nozzle height.";
            }
            if (!self.printerState.isOperational()) {
                return "The printer is not connected.";
            }
            if (!self.printerState.isReady()) {
                return "The printer is busy (printing, paused, cancelling or sending a file). Wait until it is idle.";
            }
            if (self.busy()) {
                return "Working...";
            }
            return "";
        });

        self.canAct = ko.pureComputed(function () {
            return self.disabledReason() === "";
        });

        self.storedText = ko.pureComputed(function () {
            var z = self.stored();
            return z === undefined ? "not read yet" : "M206 Z" + self.fmt(z);
        });

        self.firmwareText = ko.pureComputed(function () {
            var info = self.info();
            if (!info) return "";
            var parts = ["The firmware holds: home offset M206 Z" + self.fmt(info.z)];
            var temps = info.max_temps || {};
            var keys = Object.keys(temps).sort();
            if (keys.length) {
                parts.push(
                    "maximum temperatures " +
                        keys
                            .map(function (k) {
                                return "heater S" + k + " " + Number(temps[k]) + " °C";
                            })
                            .join(", ") +
                        " (S0 is normally the hotend, S1 the bed)"
                );
            }
            return parts.join("; ") + ".";
        });

        self.referenceNote = ko.pureComputed(function () {
            var info = self.info();
            if (!info) return "";
            if (info.settings_error) {
                return "The nozzle settings are not valid, so changes are refused: " + info.settings_error;
            }
            if (info.reference_z === null || info.reference_z === undefined) {
                return (
                    "No reference value is confirmed, so changes are refused. An administrator can enter " +
                    "the reference in Settings > EBMC Stop (the value just read is " +
                    self.fmt(info.z) +
                    ")."
                );
            }
            return (
                "Allowed range: M206 Z" +
                self.fmt(info.reference_z - info.max_total_mm) +
                " to Z" +
                self.fmt(info.reference_z + info.max_total_mm) +
                " (reference " +
                self.fmt(info.reference_z) +
                ")."
            );
        });

        self.errorText = function (jqXHR, fallback) {
            if (jqXHR && jqXHR.responseJSON && jqXHR.responseJSON.error) {
                return jqXHR.responseJSON.error;
            }
            return fallback;
        };

        self.notice = undefined;
        self.showNotice = function (options) {
            if (self.notice) self.notice.remove();
            self.notice = new PNotify(options);
        };

        self.doRead = function () {
            self.busy(true);
            self.showNotice({title: "Nozzle height", text: "Reading...", type: "info", hide: false});
            return OctoPrint.simpleApiCommand("ebmc_stop", "nozzle_height", {})
                .done(function (response) {
                    if (self.notice) self.notice.remove();
                    self.info(response);
                    self.stored(response.z);
                    if (response.steps && response.steps.length) {
                        var current = self.step();
                        self.steps(response.steps);
                        if (response.steps.indexOf(current) < 0) self.step(response.steps[0]);
                    }
                })
                .fail(function (jqXHR) {
                    self.showNotice({
                        title: "Nozzle height: read failed",
                        text: self.errorText(jqXHR, "The printer did not report its home offset."),
                        type: "error",
                        hide: false
                    });
                })
                .always(function () {
                    self.busy(false);
                });
        };

        self.read = function () {
            if (self.canAct()) self.doRead();
        };

        self.change = function (direction) {
            if (!self.canAct()) return;
            var step = Number(self.step());
            self.doRead().done(function (info) {
                var p = info.preview[direction + ":" + step];
                if (!p) {
                    self.showNotice({
                        title: "Nozzle height: not allowed",
                        text: (info.settings_error || "This step is not available.") + " Nothing was sent.",
                        type: "error",
                        hide: false
                    });
                    return;
                }
                if (!p.allowed) {
                    self.showNotice({
                        title: "Nozzle height: not allowed",
                        text: p.reason + " Nothing was sent.",
                        type: "error",
                        hide: false
                    });
                    return;
                }
                var raise = direction === "raise";
                var message =
                        "The nozzle will sit " +
                        step +
                        " mm " +
                        (raise ? "HIGHER above the bed" : "LOWER, closer to the bed") +
                        ". Stored value M206 Z will change from " +
                        self.fmt(info.z) +
                        " to " +
                        self.fmt(p.new) +
                        ". The printer will save this and home.";
                // showConfirmationDialog uses "html" instead of message and question when given.
                showConfirmationDialog({
                    title: (raise ? "RAISE" : "LOWER") + " the nozzle by " + step + " mm?",
                    html:
                        "<p>" + message + "</p><p><small>M500 saves ALL the firmware's current settings, including any the " +
                        "last print or the Terminal changed (accelerations, speeds, retraction, PID). " +
                        "Best done on a freshly reset or power-cycled printer. The settings before the " +
                        "change are written to nozzle_height.log.</small></p>" +
                        "<p>Make sure the bed is clear. Continue?</p>",
                    cancel: "No",
                    proceed: (raise ? "Yes, raise" : "Yes, lower") + " " + step + " mm",
                    proceedClass: "primary",
                    onproceed: function () {
                        self.sendAdjust(direction, step, info.z, p.new);
                    }
                });
            });
        };

        self.raise = function () {
            self.change("raise");
        };
        self.lower = function () {
            self.change("lower");
        };

        self.resultTimer = undefined;

        // "idle", "requested" (waiting for the HTTP reply), "progress" (server is
        // working) or "done". Socket messages can overtake the HTTP reply.
        self.phase = "idle";

        self.sendAdjust = function (direction, step, before, target) {
            self.busy(true);
            self.phase = "requested";
            OctoPrint.simpleApiCommand("ebmc_stop", "nozzle_adjust", {
                direction: direction,
                step_mm: step,
                expected_before: before,
                expected_target: target
            })
                .done(function (response) {
                    if (self.phase === "done") return;
                    if (self.phase === "requested") self.showNotice({
                        title: "Nozzle height",
                        text:
                            "Changing M206 Z" +
                            self.fmt(response.before) +
                            " to Z" +
                            self.fmt(response.target) +
                            "...",
                        type: "info",
                        hide: false
                    });
                    // Safety net in case the result message is lost.
                    self.resultTimer = setTimeout(function () {
                        self.busy(false);
                        self.showNotice({
                            title: "Nozzle height: no result",
                            text: "No result arrived from the server. Read the value before printing.",
                            type: "error",
                            hide: false
                        });
                    }, 200000);
                })
                .fail(function (jqXHR) {
                    self.phase = "idle";
                    self.busy(false);
                    self.showNotice({
                        title: "Nozzle height: refused",
                        text: self.errorText(jqXHR, "The change was refused."),
                        type: "error",
                        hide: false
                    });
                });
        };

        self.onDataUpdaterPluginMessage = function (plugin, data) {
            if (plugin !== "ebmc_stop" || !data || !data.type) return;
            if (data.type.indexOf("nozzle_") !== 0) return;
            if (data.type === "nozzle_progress") {
                if (self.phase === "done") return;
                self.phase = "progress";
                self.showNotice({title: "Nozzle height", text: data.text, type: "info", hide: false});
                return;
            }
            self.phase = "done";
            if (self.resultTimer) clearTimeout(self.resultTimer);
            self.resultTimer = undefined;
            self.busy(false);
            if (data.type === "nozzle_done") {
                self.stored(data.readback);
                self.showNotice({title: "Nozzle height changed", text: data.text, type: "success", hide: false});
            } else {
                self.stored(undefined);
                self.showNotice({
                    title: "Nozzle height: CHECK BEFORE PRINTING",
                    text: data.text,
                    type: "error",
                    hide: false
                });
            }
        };

        // Move the panel into the Control tab after all view models are bound.
        self.onAllBound = function () {
            $("#ebmc_nozzle_panel").appendTo("#control");
        };
    }

    OCTOPRINT_VIEWMODELS.push({
        construct: EbmcNozzleViewModel,
        dependencies: ["loginStateViewModel", "accessViewModel", "printerStateViewModel"],
        elements: ["#ebmc_nozzle_panel"]
    });
});
