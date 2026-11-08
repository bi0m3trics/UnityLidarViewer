# Convert an existing scene into spatial pages with aligned, normalized fields.
# LT01 stores compact XYZ/RGBA records followed by one float array per field.
tile_scene <- function(scene, out, tile_size = 16, page_points = 131072L) {
  if (!is.finite(tile_size) || tile_size <= 0 || !is.finite(page_points) ||
      page_points < 1 || page_points > 1000000 || page_points != floor(page_points))
    stop('Tile size must be positive and page points must be an integer from 1 to 1000000.')
  scene <- normalizePath(scene, winslash = '/', mustWork = TRUE)
  out <- normalizePath(out, winslash = '/', mustWork = FALSE)
  if (dir.exists(out) && length(list.files(out, all.files = TRUE, no.. = TRUE))) stop('Tiled output must be empty.')
  manifest <- jsonlite::read_json(file.path(scene, 'manifest.json'))
  if (manifest$version != 1 || !length(manifest$displayFields)) stop('Tiling requires a version 1 export with field sidecars. Re-export older scans first.')
  source_file <- function(name) {
    path <- normalizePath(file.path(scene, name), winslash = '/', mustWork = TRUE)
    if (!startsWith(path, paste0(scene, '/'))) stop('Scene file escapes its dataset folder.')
    path
  }
  dir.create(out, recursive = TRUE, showWarnings = FALSE)
  scratch <- tempfile('tile-build-', tmpdir = out); dir.create(scratch)
  on.exit(unlink(scratch, recursive = TRUE), add = TRUE)
  field_count <- length(manifest$displayFields)
  field_streams <- lapply(manifest$displayFields, function(field) file(source_file(field$file), 'rb'))
  on.exit(lapply(field_streams, close), add = TRUE)
  stride <- 16L + 4L * field_count
  min_x <- floor(manifest$boundsMin[[1]] / tile_size)
  min_z <- floor(manifest$boundsMin[[3]] / tile_size)
  width <- floor(manifest$boundsMax[[1]] / tile_size) - min_x + 1
  seen <- 0
  for (filename in manifest$pointFiles) {
    stream <- file(source_file(filename), 'rb')
    raw <- tryCatch({
      if (rawToChar(readBin(stream, 'raw', 4)) != 'LP01') stop('Invalid source point file.')
      n <- readBin(stream, integer(), 1, size = 4, endian = 'little')
      bytes <- readBin(stream, 'raw', n * 32)
      if (length(bytes) != n * 32) stop('Truncated source point file.')
      matrix(bytes, nrow = 32)
    }, finally = close(stream))
    records <- matrix(as.raw(0), nrow = stride, ncol = n)
    records[1:16, ] <- raw[1:16, ]
    classification <- readBin(as.raw(raw[25:28, , drop = FALSE]), integer(), n, size = 4, endian = 'little')
    records[16, classification == 2] <- as.raw(0)
    for (i in seq_len(field_count)) {
      values <- readBin(field_streams[[i]], 'raw', n * 4)
      if (length(values) != n * 4) stop('Field sidecar does not match point order or count.')
      records[16L + (i - 1L) * 4L + 1:4, ] <- values
    }
    xyz <- matrix(readBin(as.raw(raw[1:12, , drop = FALSE]), numeric(), n * 3, size = 4, endian = 'little'), ncol = 3, byrow = TRUE)
    if (any(!is.finite(xyz))) stop('Nonfinite point coordinates in scene.')
    key <- (floor(xyz[, 3] / tile_size) - min_z) * width + floor(xyz[, 1] / tile_size) - min_x
    groups <- split(seq_len(n), key)
    for (name in names(groups)) {
      stream <- file(file.path(scratch, paste0(name, '.raw')), 'ab')
      tryCatch(writeBin(as.raw(records[, groups[[name]], drop = FALSE]), stream), finally = close(stream))
    }
    seen <- seen + n
    message(sprintf('Spatial tiling: %s / %s points', seen, manifest$displayPointCount))
  }
  if (seen != manifest$displayPointCount) stop('Manifest point count mismatch.')
  for (stream in field_streams) if (length(readBin(stream, 'raw', 1))) stop('Field sidecar has extra data.')
  set.seed(20261106)
  tiles <- list()
  for (filename in sort(list.files(scratch, full.names = TRUE))) {
    stream <- file(filename, 'rb')
    tryCatch({
      repeat {
        raw <- readBin(stream, 'raw', stride * page_points)
        if (!length(raw)) break
        if (length(raw) %% stride != 0) stop('Truncated temporary tile.')
        n <- length(raw) / stride
        records <- matrix(raw, nrow = stride)
        order <- sample.int(n)
        xyz <- matrix(readBin(as.raw(records[1:12, , drop = FALSE]), numeric(), n * 3, size = 4, endian = 'little'), ncol = 3, byrow = TRUE)
        name <- sprintf('tile_%05d.bytes', length(tiles))
        output <- file(file.path(out, name), 'wb')
        tryCatch({
          writeBin(charToRaw('LT01'), output)
          writeBin(as.integer(c(n, field_count)), output, size = 4, endian = 'little')
          writeBin(as.raw(records[1:16, order, drop = FALSE]), output)
          for (i in seq_len(field_count))
            writeBin(as.raw(records[16L + (i - 1L) * 4L + 1:4, order, drop = FALSE]), output)
        }, finally = close(output))
        tiles[[length(tiles) + 1L]] <- list(file = name, count = n,
          boundsMin = apply(xyz, 2, min), boundsMax = apply(xyz, 2, max))
      }
    }, finally = close(stream))
    unlink(filename)
  }
  # Geometry and collision remain independent of render density and streaming.
  copy_names <- unique(c(manifest$groundFile, unlist(manifest$collisionFiles), manifest$slimCollisionFile, manifest$generatedDem))
  for (name in copy_names) {
    source <- source_file(name)
    target <- file.path(out, name)
    dir.create(dirname(target), recursive = TRUE, showWarnings = FALSE)
    if (!file.copy(source, target)) stop('Could not copy scene geometry: ', name)
  }
  manifest$version <- 2L
  manifest$tileSize <- tile_size
  manifest$tiles <- tiles
  manifest$pointFiles <- I(character())
  manifest$displayFields <- lapply(manifest$displayFields, function(field) list(name = field$name))
  # Keep all source metadata arrays as arrays even for a single file or field.
  manifest$collisionFiles <- I(as.character(unlist(manifest$collisionFiles)))
  manifest$availableFields <- I(as.character(unlist(manifest$availableFields)))
  jsonlite::write_json(manifest, file.path(out, 'manifest.json'), auto_unbox = TRUE, pretty = TRUE, digits = NA, null = 'null')
  message(sprintf('Tiled %s points into %s spatial pages at %s', seen, length(tiles), out))
  invisible(manifest)
}

if (sys.nframe() == 0) {
  script <- sub('^--file=', '', grep('^--file=', commandArgs(), value = TRUE)[1])
  library_path <- file.path(dirname(normalizePath(script)), '.r-library')
  if (dir.exists(library_path)) .libPaths(c(library_path, .libPaths()))
  args <- commandArgs(trailingOnly = TRUE)
  if (length(args) < 3 || args[2] != '--out') stop('Usage: Rscript tile_scene.R scene-folder --out tiled-folder [--tile-size metres] [--page-points count]')
  tile_size <- 16; page_points <- 131072L
  if (length(args) > 3) {
    if ((length(args) - 3) %% 2 != 0) stop('Missing tiling option value.')
    for (i in seq.int(4, length(args), by = 2)) {
      if (args[i] == '--tile-size') tile_size <- as.numeric(args[i + 1]) else
        if (args[i] == '--page-points') page_points <- as.numeric(args[i + 1]) else stop('Unknown tiling option ', args[i])
    }
  }
  tile_scene(args[1], args[3], tile_size, page_points)
}
