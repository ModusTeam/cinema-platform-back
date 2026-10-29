using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.DTOs.Movies;

public sealed class UploadMovieImageRequest
{
    [Required, FromForm(Name = "file")]
    public IFormFile? File { get; init; }

    [Required, FromForm(Name = "kind")]
    [RegularExpression("^(poster|backdrop)$")]
    public string? Kind { get; init; }
}
