/*
 * EBMC Brand: point the page's icon links at the club badge.
 *
 * index.jinja2 hard-codes the favicon, apple-touch icons, pinned-tab icons and the web app
 * manifest to OctoPrint's static files, and OctoPrint offers no hook for them. This runs when
 * the plugin bundle loads, before OctoPrint's appearance view model is built, so that view
 * model's favicon recolouring (Settings > Appearance > "Colorize favicon") starts from the
 * badge (it reads link[rel~='mask-icon-theme']).
 */
(function () {
    var icon = document.querySelector("link[rel='shortcut icon']");
    if (!icon || !icon.href) return;
    // .../static/img/tentacle-32x32.png -> .../plugin/ebmc_brand/static/img/
    var idx = icon.href.lastIndexOf("static/img/");
    if (idx < 0) return;
    var base = icon.href.substring(0, idx) + "plugin/ebmc_brand/static/";
    var img = base + "img/";

    function set(selector, href) {
        var nodes = document.querySelectorAll(selector);
        for (var i = 0; i < nodes.length; i++) nodes[i].href = href;
    }

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
})();
