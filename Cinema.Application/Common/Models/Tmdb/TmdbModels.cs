using System.Text.Json.Serialization;

namespace Cinema.Application.Common.Models.Tmdb;

public class TmdbSearchResponse
{
    [JsonPropertyName("results")]
    public List<TmdbMovieResult> Results { get; set; } = [];
}

public class TmdbMovieResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
}

public class TmdbMovieDetails
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("genres")]
    public List<TmdbGenreDto> Genres { get; set; } = [];

    [JsonPropertyName("credits")]
    public TmdbCredits? Credits { get; set; }
    
    [JsonPropertyName("videos")]
    public TmdbVideos? Videos { get; set; } 
}

public class TmdbGenreDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class TmdbCredits
{
    [JsonPropertyName("cast")]
    public List<TmdbCastDto> Cast { get; set; } = [];
}

public class TmdbCastDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("character")]
    public string? Character { get; set; }
    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
    [JsonPropertyName("order")]
    public int Order { get; set; }
}

public class TmdbVideos
{
    [JsonPropertyName("results")]
    public List<TmdbVideoDto> Results { get; set; } = [];
}

public class TmdbVideoDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string Site { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}