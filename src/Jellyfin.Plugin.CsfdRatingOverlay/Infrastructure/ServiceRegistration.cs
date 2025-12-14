using Jellyfin.Plugin.CsfdRatingOverlay.Infrastructure;
using MediaBrowser.Controller.Plugins;

// Register the plugin's DI registrations with Jellyfin (required for 10.9+).
[assembly: PluginServiceRegistration(typeof(ServiceRegistrator))]
