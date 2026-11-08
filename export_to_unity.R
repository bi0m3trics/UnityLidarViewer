#!/usr/bin/env Rscript
# Export a LAS/LAZ and optional elevation raster to the existing Unity scene format.
export_script <- if (sys.nframe() > 0 && !is.null(sys.frame(1)$ofile)) sys.frame(1)$ofile else
  sub('^--file=', '', grep('^--file=', commandArgs(), value = TRUE)[1])
export_root <- normalizePath(dirname(export_script), winslash = '/', mustWork = TRUE)
local_library <- file.path(export_root, '.r-library')
if (dir.exists(local_library)) .libPaths(c(local_library, .libPaths()))
required_packages <- c('rlas', 'terra', 'data.table', 'jsonlite', 'geometry', 'RANN')
missing_packages <- required_packages[!vapply(required_packages, requireNamespace, logical(1), quietly = TRUE)]
if (length(missing_packages)) stop('Missing R packages: ', paste(missing_packages, collapse = ', '),
  '. Run Rscript install_dependencies.R first.', call. = FALSE)
library(data.table)
source(file.path(export_root, 'las_io.R'))
source(file.path(export_root, 'ptd_ground.R'))
source(file.path(export_root, 'slim_collision.R'))
source(file.path(export_root, 'tile_scene.R'))

parse_options <- function(argv) {
  defaults <- list(out = 'UnityProject/Assets/StreamingAssets/LidarScene', field = 'intensity',
    max_points = 2000000, voxel = .5, min_voxel_points = 5, max_voxels = 2000000,
    ground_clearance = .25, dem_stride = 2, scale = 1, dem_resolution = .5,
    ptd_seed_size = 8, ptd_distance = .45, ptd_angle = 15, ply = FALSE, streaming = FALSE, tile_size = 16)
  if (!length(argv) || any(argv %in% c('--help', '-h'))) {
    cat('Usage: Rscript export_to_unity.R input.laz [dem.tif] --out folder [options]\n',
      'Omit dem.tif to estimate terrain using progressive TIN densification.\n',
      'Options and defaults\n', sep = '')
    for (name in names(defaults)) cat('  --', gsub('_', '-', name), '  ', as.character(defaults[[name]]), '\n', sep = '')
    return(NULL)
  }
  positional <- character(); i <- 1L
  while (i <= length(argv)) {
    value <- argv[i]
    if (!startsWith(value, '--')) { positional <- c(positional, value); i <- i + 1L; next }
    name <- gsub('-', '_', substring(value, 3))
    if (!name %in% names(defaults)) stop('Unknown option ', value)
    if (name %in% c('ply', 'streaming')) { defaults[[name]] <- TRUE; i <- i + 1L; next }
    if (i == length(argv)) stop('Missing value for ', value)
    defaults[[name]] <- if (is.numeric(defaults[[name]])) suppressWarnings(as.numeric(argv[i + 1L])) else argv[i + 1L]
    i <- i + 2L
  }
  if (!length(positional) || length(positional) > 2) stop('Supply a LAS/LAZ and optionally a DEM.')
  defaults$laz <- normalizePath(positional[1], mustWork = TRUE)
  defaults$dem <- if (length(positional) == 2) normalizePath(positional[2], mustWork = TRUE) else NULL
  positive <- c('max_points', 'voxel', 'min_voxel_points', 'max_voxels', 'dem_stride', 'scale',
    'dem_resolution', 'ptd_seed_size', 'ptd_distance', 'ptd_angle', 'tile_size')
  for (name in positive) if (!is.finite(defaults[[name]]) || defaults[[name]] <= 0) stop(name, ' must be finite and positive.')
  for (name in c('max_points', 'min_voxel_points', 'max_voxels', 'dem_stride'))
    if (defaults[[name]] != floor(defaults[[name]])) stop(name, ' must be an integer.')
  if (!is.finite(defaults$ground_clearance) || defaults$ground_clearance < 0) stop('Ground clearance must be finite and nonnegative.')
  if (defaults$dem_resolution > defaults$ptd_seed_size || defaults$ptd_angle >= 90)
    stop('PTD needs resolution <= seed size and an angle between 0 and 90 degrees.')
  defaults
}

append_display_fields <- function(out, fields, points, ids, local, height) {
  for (i in seq_along(fields)) {
    name <- fields[i]
    if (is.na(name)) values <- height[ids] else if (name %in% c('X', 'Y', 'Z')) {
      values <- local[ids, c(X = 1, Y = 3, Z = 2)[name]]
    } else {
      key <- gsub('[^a-z0-9]', '', tolower(name))
      values <- if (key == 'classification' || endsWith(key, 'id')) categorical_values(points[[name]], ids) else points[[name]][ids]
    }
    stream <- file(file.path(out, sprintf('field_%03d.bytes', i - 1L)), 'ab')
    tryCatch(writeBin(as.double(values), stream, size = 4, endian = 'little'), finally = close(stream))
  }
}

finish_display_fields <- function(out, fields, count) {
  result <- list()
  for (i in seq_along(fields)) {
    filename <- sprintf('field_%03d.bytes', i - 1L)
    path <- file.path(out, filename)
    stream <- file(path, 'rb')
    values <- tryCatch(readBin(stream, numeric(), count, size = 4, endian = 'little'), finally = close(stream))
    if (length(values) != count) stop('Field sidecar length mismatch.')
    valid <- is.finite(values)
    if (!any(valid)) { unlink(path); next }
    key <- if (is.na(fields[i])) '' else gsub('[^a-z0-9]', '', tolower(fields[i]))
    if (key != 'classification' && !endsWith(key, 'id')) {
      limits <- quantile(values[valid], c(.02, .98), names = FALSE)
      values[valid] <- if (limits[2] > limits[1]) pmax(0, pmin(1, (values[valid] - limits[1]) / diff(limits))) else .5
    }
    values[!valid] <- -1
    stream <- file(path, 'wb')
    tryCatch(writeBin(as.double(values), stream, size = 4, endian = 'little'), finally = close(stream))
    result[[length(result) + 1L]] <- list(name = if (is.na(fields[i])) 'HAG (from DEM)' else fields[i], file = filename)
  }
  result
}

write_ply <- function(out, files, count) {
  stream <- file(file.path(out, 'cloud.ply'), 'wb'); on.exit(close(stream))
  header <- paste0('ply\nformat binary_little_endian 1.0\nelement vertex ', count,
    '\nproperty float x\nproperty float y\nproperty float z\nproperty uchar red\nproperty uchar green\nproperty uchar blue\n',
    'property float intensity\nproperty float scalar\nproperty uchar classification\nend_header\n')
  writeBin(charToRaw(header), stream)
  for (filename in files) {
    input <- file(file.path(out, filename), 'rb')
    tryCatch({
      if (rawToChar(readBin(input, 'raw', 4)) != 'LP01') stop('Invalid point file.')
      n <- readBin(input, integer(), 1, size = 4, endian = 'little')
      records <- matrix(readBin(input, 'raw', n * 32), nrow = 32)
      writeBin(as.raw(records[c(1:15, 17:24, 25), , drop = FALSE]), stream)
    }, finally = close(input))
  }
}

export_scene <- function(args) {
  out <- normalizePath(args$out, winslash = '/', mustWork = FALSE)
  if (dir.exists(out) && length(list.files(out, all.files = TRUE, no.. = TRUE))) stop('Output folder must be empty to avoid mixing exports.')
  dir.create(dirname(out), recursive = TRUE, showWarnings = FALSE)
  temp <- tempfile('lidar-r-', tmpdir = dirname(out)); dir.create(temp)
  on.exit(unlink(temp, recursive = TRUE), add = TRUE)
  destination <- out
  if (isTRUE(args$streaming)) out <- file.path(temp, 'scene')
  las <- args$laz
  input <- file(las, 'rb'); prefix <- readBin(input, 'raw', 105); close(input)
  if (length(prefix) < 105 || rawToChar(prefix[1:4]) != 'LASF') stop('Input must be a LAS/LAZ file.')
  if (bitwAnd(as.integer(prefix[105]), 128L) != 0) {
    las <- file.path(temp, 'source.las')
    message('Decompressing LAZ to a temporary LAS for bounded chunk reads...')
    rlas::read_and_write.las(args$laz, las, filter = '-keep_every_nth 1')
  }
  header <- rlas::read.lasheader(las)
  schema <- las_schema(header)
  fields <- vapply(schema, `[[`, '', 'name')
  scalar_fields <- fields[vapply(schema, function(field) field$elements == 1, logical(1))]
  if (!args$field %in% scalar_fields) stop('Unknown or nonscalar field. Available scalar fields: ', paste(scalar_fields, collapse = ', '))
  source_crs <- las_crs(header)
  if (nzchar(source_crs) && terra::is.lonlat(source_crs)) stop('Geographic degrees are unsupported. Use projected or local coordinates.')
  generated <- is.null(args$dem)
  dem_path <- args$dem
  if (generated) {
    dem_path <- file.path(temp, 'generated-dem.tif')
    build_dem(las, header, schema, dem_path, args)
  }
  raster <- terra::rast(dem_path, guessCRS = FALSE)[[1]]
  if (terra::is.rotated(raster)) raster <- terra::rectify(raster)
  if (nzchar(terra::crs(raster)) && isTRUE(terra::is.lonlat(raster))) stop('Geographic DEM coordinates are unsupported.')
  raster_crs <- terra::crs(raster)
  if (nzchar(source_crs) && nzchar(raster_crs) && !terra::same.crs(raster, source_crs)) stop('CRS mismatch. Reproject inputs first.')
  if (!nzchar(source_crs) || !nzchar(raster_crs)) message('WARNING: missing CRS tag; assuming inputs share coordinates and vertical datum.')
  if (!(header[['Min X']] < terra::xmax(raster) && header[['Max X']] > terra::xmin(raster) &&
        header[['Min Y']] < terra::ymax(raster) && header[['Max Y']] > terra::ymin(raster))) stop('LAS/LAZ and DEM XY bounds do not overlap.')
  origin <- c((header[['Min X']] + header[['Max X']]) / 2, (header[['Min Y']] + header[['Max Y']]) / 2, header[['Min Z']])
  values <- as.numeric(terra::values(raster))
  mesh <- dem_mesh(raster, values, origin, args$scale, args$dem_stride)
  dir.create(out, recursive = TRUE, showWarnings = FALSE)
  write_mesh(file.path(out, 'ground.bytes'), mesh$vertices, mesh$triangles)
  source_count <- header[['Extended Number of point records']]
  if (is.null(source_count) || source_count == 0) source_count <- header[['Number of point records']]
  step <- max(1, ceiling(source_count / args$max_points))
  counts <- data.table(x = numeric(), y = numeric(), z = numeric(), count = integer())
  columns <- data.table(x = numeric(), z = numeric(), band = numeric(), count = integer())
  point_files <- character(); total <- 0; class_counts <- numeric(); stats <- list(); rgb_scale <- NULL
  display_fields <- c(scalar_fields, NA_character_)
  read_las_chunks(las, header, schema, function(points, seen, n) {
    xyz <- las_coordinates(points, header)
    usable <- is.finite(xyz[, 1]) & is.finite(xyz[, 2]) & is.finite(xyz[, 3]) & points$withheld == 0
    cl <- points$classification
    classes <- table(cl)
    for (key in names(classes)) {
      previous <- class_counts[key]
      if (is.na(previous)) previous <- 0
      class_counts[key] <<- previous + as.numeric(classes[key])
    }
    local <- sweep(xyz[, c(1, 3, 2), drop = FALSE], 2, origin[c(1, 3, 2)]) * args$scale
    ground <- sample_dem(raster, values, xyz[, 1], xyz[, 2])
    height <- (xyz[, 3] - ground) * args$scale
    solid <- usable & !cl %in% c(2, 7, 18)
    new_columns <- collect_columns(local, height, solid)
    columns <<- rbindlist(list(columns, new_columns))[, list(count = sum(count)), by = c('x', 'z', 'band')]
    obstacle <- which(solid & (!is.finite(ground) | height > args$ground_clearance))
    voxels <- floor(local[obstacle, , drop = FALSE] / args$voxel)
    new_counts <- data.table(x = voxels[, 1], y = voxels[, 2], z = voxels[, 3])[, list(count = .N), by = c('x', 'y', 'z')]
    counts <<- rbindlist(list(counts, new_counts))[, list(count = sum(count)), by = c('x', 'y', 'z')]
    if (nrow(counts) > args$max_voxels) stop('Collision voxel budget exceeded. Increase --voxel or --max-voxels.')
    ids <- which(usable & ((seq_len(n) - 1 + seen) %% step == 0))
    if (length(ids)) {
      rgba <- matrix(180, length(ids), 4); rgba[, 4] <- 255
      if ('red' %in% fields) {
        rgb <- cbind(points$red, points$green, points$blue)
        if (is.null(rgb_scale) && max(rgb) > 0) rgb_scale <<- if (max(rgb) > 255) 257 else 1
        rgba[, 1:3] <- floor(pmax(0, pmin(255, rgb[ids, , drop = FALSE] / if (is.null(rgb_scale)) 257 else rgb_scale)))
      }
      if (generated) rgba[which(is.finite(height[ids]) & abs(height[ids]) <= .20), 4] <- 0
      filename <- sprintf('points_%04d.bytes', length(point_files))
      write_points(file.path(out, filename), local[ids, , drop = FALSE], rgba,
        points$intensity[ids], points[[args$field]][ids], cl[ids], points$return_number[ids])
      append_display_fields(out, display_fields, points, ids, local, height)
      point_files <<- c(point_files, filename)
      scalar <- points[[args$field]][ids]
      stats[[length(stats) + 1L]] <<- scalar[is.finite(scalar)]
      total <<- total + length(ids)
    }
    message(sprintf('Points %s/%s; display %s; occupied candidates %s', seen + n, source_count, total, nrow(counts)))
  })
  point_files <- pack_point_files(out, point_files)
  collisions <- collision_meshes(counts, args$min_voxel_points, args$voxel, out)
  ground_at <- function(x, y) sample_dem(raster, values, x, y)
  slim <- write_slim_scene(out, columns, ground_at, origin, args$scale)
  centres <- (mesh$vertices[mesh$triangles[, 1], , drop = FALSE] + mesh$vertices[mesh$triangles[, 2], , drop = FALSE] +
    mesh$vertices[mesh$triangles[, 3], , drop = FALSE]) / 3
  occupied <- new.env(hash = TRUE, parent = emptyenv())
  for (i in which(counts$count >= args$min_voxel_points)) occupied[[paste(counts$x[i], counts$y[i], counts$z[i], sep = ',')]] <- TRUE
  spawn <- NULL
  for (i in order(centres[, 1] ^ 2 + centres[, 3] ^ 2)) {
    feet <- centres[i, ]
    lo <- floor((feet + c(-.4, .15, -.4)) / args$voxel)
    hi <- floor((feet + c(.4, 2.2, .4)) / args$voxel)
    cells <- expand.grid(x = seq(lo[1], hi[1]), y = seq(lo[2], hi[2]), z = seq(lo[3], hi[3]))
    keys <- paste(cells$x, cells$y, cells$z, sep = ',')
    if (!any(vapply(keys, exists, logical(1), envir = occupied, inherits = FALSE))) { spawn <- feet + c(0, .2, 0); break }
  }
  if (is.null(spawn)) stop('No clear player spawn found. Review DEM and collision settings.')
  scalar <- unlist(stats, use.names = FALSE)
  if (!length(scalar)) stop('No finite display field values.')
  limits <- quantile(scalar, c(.02, .98), names = FALSE)
  if (limits[2] <= limits[1]) limits[2] <- limits[1] + 1
  manifest <- list(version = 1L, sourcePointCount = source_count, displayPointCount = total,
    origin = origin, scale = args$scale, axisMapping = 'Unity (x,y,z) = source (X-originX,Z-originZ,Y-originY) * scale',
    field = args$field, availableFields = I(fields), fieldMin = limits[1], fieldMax = limits[2],
    displayFields = finish_display_fields(out, display_fields, total), hasRgb = 'red' %in% fields,
    pointFiles = I(point_files), groundFile = 'ground.bytes', collisionFiles = I(collisions$files),
    slimCollisionFile = slim$file, slimCollisionCount = slim$count, voxelSize = args$voxel,
    collisionVoxelCount = collisions$count, spawn = spawn,
    boundsMin = (c(header[['Min X']], header[['Min Z']], header[['Min Y']]) - origin[c(1, 3, 2)]) * args$scale,
    boundsMax = (c(header[['Max X']], header[['Max Z']], header[['Max Y']]) - origin[c(1, 3, 2)]) * args$scale,
    classes = as.list(class_counts), crs = if (nzchar(source_crs)) source_crs else NULL,
    converter = 'R', assumptions = I(c('Inputs share horizontal coordinates and vertical datum.',
      'Scale converts source units to metres.', 'Collision approximates non-ground, non-noise, non-withheld returns.',
      'DEM NoData cells remain holes.')))
  if (generated) {
    if (!file.copy(dem_path, file.path(out, 'generated-dem.tif'))) stop('Could not copy the generated terrain.')
    manifest$generatedDem <- 'generated-dem.tif'
  }
  if (args$ply) write_ply(out, point_files, total)
  # Publish the manifest last, so incomplete exports cannot be opened as scenes.
  jsonlite::write_json(manifest, file.path(out, 'manifest.json'), pretty = TRUE, auto_unbox = TRUE, digits = NA, null = 'null')
  if (isTRUE(args$streaming)) {
    manifest <- tile_scene(out, destination, args$tile_size)
    if (args$ply && !file.copy(file.path(out, 'cloud.ply'), file.path(destination, 'cloud.ply')))
      stop('Could not copy the optional PLY.')
    out <- destination
  }
  message(sprintf('Exported %s display points and %s collision voxels to %s', total, collisions$count, out))
  invisible(manifest)
}

if (sys.nframe() == 0) {
  status <- tryCatch({ options <- parse_options(commandArgs(trailingOnly = TRUE)); if (!is.null(options)) export_scene(options); 0L },
    error = function(error) { message('Export failed: ', conditionMessage(error)); 1L })
  quit(status = status)
}
