using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;

namespace BabyMonitarr.Backend.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IAudioProcessingService _audioService;

        public HomeController(ILogger<HomeController> logger, IAudioProcessingService audioService)
        {
            _logger = logger;
            _audioService = audioService;
        }

        public IActionResult Index()
        {
            // Send the current settings to the view
            return View(_audioService.GetSettings());
        }

        [HttpPost]
        public IActionResult UpdateSettings(AudioSettings settings)
        {
            if (ModelState.IsValid)
            {
                _audioService.UpdateSettings(settings);
                return RedirectToAction(nameof(Index));
            }
            return View("Index", settings);
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}