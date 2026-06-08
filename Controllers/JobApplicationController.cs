using Microsoft.AspNetCore.Mvc;
using JobTrackerApi.Models;
using JobTrackerApi.Services;

namespace JobTrackerApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobApplicationController : BaseController
{
    private readonly JobApplicationService _jobApplicationService;

    public JobApplicationController(JobApplicationService jobApplicationService) =>
        _jobApplicationService = jobApplicationService;

    // GET: Get all job applications for the authenticated user
    [HttpGet]
    [Route("/api/jobapplications")]
    public async Task<ActionResult<List<JobApplication>>> GetAll()
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { message = "User not authenticated" });
        }

        var jobApplications = await _jobApplicationService.GetByUserAsync(userId);

        return Ok(jobApplications);
    }

    // GET: Get specific job application by ID
    [HttpGet("{id:length(24)}")]
    public async Task<ActionResult<JobApplication>> Get(string id)
    {
        var userId = GetUserId();
        var jobApplication = await _jobApplicationService.GetAsync(id);

        if (jobApplication == null)
        {
            return NotFound();
        }

        // Verify the job application belongs to the user
        if (jobApplication.userId != userId)
        {
            return Forbid();
        }

        return jobApplication;
    }

    // POST: Create new job application
    [HttpPost]
    [Route("/api/jobapplications")] // Match the singular route your frontend expects
    public async Task<IActionResult> Post([FromBody] JobApplication jobApplication)
    {
        var userId = GetUserId();

        // Ensure the userId matches the authenticated user
        jobApplication.userId = userId;

        // Validate model state
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // Validate that userId is provided
        if (string.IsNullOrEmpty(jobApplication.userId))
        {
            return BadRequest(new { message = "User ID is required" });
        }

        // Set default values if not provided
        if (jobApplication.applicationDate == default)
        {
            jobApplication.applicationDate = DateTime.UtcNow;
        }

        await _jobApplicationService.CreateAsync(jobApplication);

        return CreatedAtAction(nameof(Get), new { id = jobApplication.Id }, jobApplication);
    }

    // PUT: Update job application
    [HttpPut("{id:length(24)}")]
    // [Route("/api/jobapplication/{id:length(24)}")] // Match singular route
    public async Task<IActionResult> Update(string id, [FromBody] JobApplication updatedJobApplication)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { message = "User not authenticated" });
        }

        // Validate model state
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var existingJobApplication = await _jobApplicationService.GetAsync(id);

        if (existingJobApplication is null)
        {
            return NotFound(new { message = "Job application not found" });
        }

        // Verify the job application belongs to the user
        if (existingJobApplication.userId != userId)
        {
            return Forbid();
        }

        // Preserve the original ID and userId
        updatedJobApplication.Id = id;
        updatedJobApplication.userId = existingJobApplication.userId;

        // Preserve JobNumber if it exists
        updatedJobApplication.jobNumber = existingJobApplication.jobNumber;

        await _jobApplicationService.UpdateAsync(id, updatedJobApplication);

        return Ok(updatedJobApplication);
    }

    // DELETE: Delete job application
    [HttpDelete("{id:length(24)}")]
    // [Route("/api/jobapplication/{id:length(24)}")] // Match singular route
    public async Task<IActionResult> Delete(string id)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { message = "User not authenticated" });
        }

        var jobApplication = await _jobApplicationService.GetAsync(id);

        if (jobApplication is null)
        {
            return NotFound(new { message = "Job application not found" });
        }

        // Verify the job application belongs to the user
        if (jobApplication.userId != userId)
        {
            return Forbid();
        }

        await _jobApplicationService.RemoveAsync(id);

        return Ok(new { message = "Job application deleted successfully" });
    }

    // DELETE: Delete all job applications for the authenticated user
    [HttpDelete("user/{userId}")]
    public async Task<IActionResult> DeleteAllForUser(string userId)
    {
        var callerId = GetUserId();
        if (string.IsNullOrEmpty(callerId))
        {
            return Unauthorized(new { message = "User not authenticated" });
        }

        // A user may only bulk-delete their own applications
        if (userId != callerId)
        {
            return Forbid();
        }

        var deletedCount = await _jobApplicationService.RemoveAllForUserAsync(callerId);

        return Ok(new
        {
            message = $"Deleted {deletedCount} job application(s) for user",
            count = deletedCount
        });
    }

    // PATCH: Update only status (useful for quick updates)
    [HttpPatch("{id:length(24)}/status")]
    public async Task<IActionResult> UpdateStatus(string id, [FromBody] StatusUpdateRequest request)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { message = "User not authenticated" });
        }

        var jobApplication = await _jobApplicationService.GetAsync(id);

        if (jobApplication is null)
        {
            return NotFound(new { message = "Job application not found" });
        }

        // Verify the job application belongs to the user
        if (jobApplication.userId != userId)
        {
            return Forbid();
        }

        jobApplication.status = request.Status;
        jobApplication.autoStatusUpdated = request.AutoStatusUpdated;

        await _jobApplicationService.UpdateAsync(id, jobApplication);

        return Ok(jobApplication);
    }
}

// Helper class for status updates
public class StatusUpdateRequest
{
    public string Status { get; set; } = string.Empty;
    public bool AutoStatusUpdated { get; set; } = false;
}