/*
 * EBMC Brand.
 *
 * 1. Point the page's icon links at the club badge. index.jinja2 hard-codes the favicon,
 *    apple-touch icons, pinned-tab icons and the web app manifest to OctoPrint's static
 *    files, and OctoPrint offers no hook for them. This runs when the plugin bundle loads,
 *    before OctoPrint's appearance view model is built, so that view model's favicon
 *    recolouring (Settings > Appearance > "Colorize favicon") starts from the badge (it
 *    reads link[rel~='mask-icon-theme']).
 * 2. Colours that live in canvas drawing options rather than CSS: the temperature graph's
 *    frame, axis labels and grid (flot "grid" options) and the G-code viewer's grid
 *    (renderer option "colorGrid"). The colours are read from the palette's CSS variables.
 *    Graph line colours are not touched.
 */
(function () {
    var icon = document.querySelector("link[rel='shortcut icon']");
    if (icon && icon.href) {
        // .../static/img/tentacle-32x32.png -> .../plugin/ebmc_brand/static/img/
        var idx = icon.href.lastIndexOf("static/img/");
        if (idx >= 0) {
            var base = icon.href.substring(0, idx) + "plugin/ebmc_brand/static/";
            var img = base + "img/";

            var set = function (selector, href) {
                var nodes = document.querySelectorAll(selector);
                for (var i = 0; i < nodes.length; i++) nodes[i].href = href;
            };

            set("link[rel='shortcut icon']", img + "favicon-32x32.png");
            set("link[rel='mask-icon']", img + "badge.svg");
            set("link[rel='mask-icon-theme']", img + "favicon-256x256.png");
            set("link[rel='apple-touch-icon'][sizes='114x114']", img + "apple-touch-icon-114x114.png");
            set("link[rel='apple-touch-icon'][sizes='144x144']", img + "apple-touch-icon-144x144.png");
            set("link[rel='manifest']", base + "manifest.json");

            var touch = document.createElement("link");
            touch.rel = "apple-touch-icon";
            touch.sizes = "180x180";
            touch.href = img + "apple-touch-icon-180x180.png";
            document.head.appendChild(touch);
        }
    }

    function palette(name) {
        return getComputedStyle(document.documentElement).getPropertyValue("--ebmc-" + name).trim();
    }

    // Temperature graph (temperature.js builds it with $.plot and flot's default grid colours).
    if (window.jQuery && jQuery.plot && !jQuery.plot.ebmcWrapped) {
        var originalPlot = jQuery.plot;
        var wrappedPlot = function (target, data, options) {
            if (jQuery(target).is("#temperature-graph") && palette("ink")) {
                options = jQuery.extend(true, {}, options || {}, {
                    grid: {
                        color: palette("soft-ink"),
                        borderColor: palette("ink"),
                        tickColor: palette("grid-line")
                    }
                });
            }
            return originalPlot.call(this, target, data, options);
        };
        for (var key in originalPlot) {
            if (Object.prototype.hasOwnProperty.call(originalPlot, key)) wrappedPlot[key] = originalPlot[key];
        }
        wrappedPlot.ebmcWrapped = true;
        jQuery.plot = wrappedPlot;
    }

    // G-code viewer grid and bed outline (renderer.js "colorGrid"; its public setOption()).
    function gcodeColours() {
        if (window.GCODE && GCODE.renderer && GCODE.renderer.setOption && palette("line")) {
            GCODE.renderer.setOption({colorGrid: palette("line")});
        }
    }
    gcodeColours();
    if (window.jQuery) jQuery(gcodeColours);
})();
