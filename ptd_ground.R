# Grid-thinned progressive TIN densification in local metre coordinates.
build_dem <- function(las, header, schema, destination, args) {
  cell <- args$dem_resolution / args$scale
  width <- floor((header[['Max X']] - header[['Min X']]) / cell) + 1L
  height <- floor((header[['Max Y']] - header[['Min Y']]) / cell) + 1L
  if (width * height > 2000000) stop('PTD grid is too large. Increase --dem-resolution or supply a DEM.')
  minima <- rep(Inf, width * height)
  terrain_schema <- schema[vapply(schema, function(field) field$name %in% c('X', 'Y', 'Z', 'withheld', 'classification'), logical(1))]
  read_las_chunks(las, header, terrain_schema, function(points, seen, n) {
    xyz <- las_coordinates(points, header)
    good <- is.finite(xyz[, 1]) & is.finite(xyz[, 2]) & is.finite(xyz[, 3]) &
      points$withheld == 0 & !points$classification %in% c(7, 18)
    col <- floor((xyz[good, 1] - header[['Min X']]) / cell)
    row <- floor((header[['Max Y']] - xyz[good, 2]) / cell)
    values <- data.table::data.table(index = row * width + col + 1L,
      z = (xyz[good, 3] - header[['Min Z']]) * args$scale)
    values <- values[index >= 1 & index <= length(minima), list(z = min(z)), by = index]
    minima[values$index] <<- pmin(minima[values$index], values$z)
  })
  grid <- matrix(minima, nrow = height, ncol = width, byrow = TRUE)
  neighbors <- matrix(Inf, height, width)
  for (dr in -1:1) for (dc in -1:1) {
    if (dr == 0 && dc == 0) next
    rows <- seq_len(height); cols <- seq_len(width)
    rows <- rows[rows + dr >= 1 & rows + dr <= height]
    cols <- cols[cols + dc >= 1 & cols + dc <= width]
    neighbors[rows, cols] <- pmin(neighbors[rows, cols, drop = FALSE], grid[rows + dr, cols + dc, drop = FALSE])
  }
  grid[is.finite(neighbors) & grid + max(.75, 2 * args$dem_resolution) < neighbors] <- Inf
  cells <- which(is.finite(grid), arr.ind = TRUE)
  if (nrow(cells) < 3) stop('Too few ground candidates for PTD.')
  row <- cells[, 1] - 1L; col <- cells[, 2] - 1L
  xy <- cbind((col + .5) * args$dem_resolution, -(row + .5) * args$dem_resolution)
  z <- grid[cells]
  stride <- max(1, round(args$ptd_seed_size / args$dem_resolution))
  coarse <- floor(row / stride) * ceiling(width / stride) + floor(col / stride)
  ranked <- order(coarse, z)
  accepted <- rep(FALSE, length(z))
  accepted[ranked[!duplicated(coarse[ranked])]] <- TRUE
  angle_limit <- sin(args$ptd_angle * pi / 180)
  for (iteration in seq_len(20)) {
    nodes <- which(accepted)
    if (length(nodes) < 3) stop('Too few PTD seeds. Reduce --ptd-seed-size or supply a DEM.')
    tin <- geometry::delaunayn(xy[nodes, , drop = FALSE])
    pending <- which(!accepted)
    if (!length(pending)) break
    lookup <- geometry::tsearch(xy[nodes, 1], xy[nodes, 2], tin, xy[pending, 1], xy[pending, 2], bary = TRUE)
    inside <- which(!is.na(lookup$idx))
    if (!length(inside)) break
    pending <- pending[inside]
    triangle <- lookup$idx[inside]
    vertices <- matrix(nodes[tin[triangle, , drop = FALSE]], ncol = 3)
    heights <- matrix(z[vertices], ncol = 3)
    predicted <- rowSums(lookup$p[inside, , drop = FALSE] * heights)
    ax <- xy[vertices[, 1], 1] - xy[vertices[, 3], 1]
    ay <- xy[vertices[, 1], 2] - xy[vertices[, 3], 2]
    bx <- xy[vertices[, 2], 1] - xy[vertices[, 3], 1]
    by <- xy[vertices[, 2], 2] - xy[vertices[, 3], 2]
    az <- heights[, 1] - heights[, 3]; bz <- heights[, 2] - heights[, 3]
    determinant <- ax * by - ay * bx
    gx <- (az * by - bz * ay) / determinant
    gy <- (bz * ax - az * bx) / determinant
    distance <- abs(z[pending] - predicted) / sqrt(1 + gx ^ 2 + gy ^ 2)
    horizontal <- sapply(1:3, function(j) rowSums((xy[pending, , drop = FALSE] - xy[vertices[, j], , drop = FALSE]) ^ 2))
    horizontal <- matrix(horizontal, ncol = 3)
    nearest <- max.col(-horizontal, ties.method = 'first')
    at <- cbind(seq_along(pending), nearest)
    vertex_distance <- sqrt(horizontal[at] + (z[pending] - heights[at]) ^ 2)
    eligible <- which(distance <= args$ptd_distance & distance <= angle_limit * vertex_distance)
    if (!length(eligible)) break
    ranked <- eligible[order(triangle[eligible], distance[eligible])]
    accepted[pending[ranked[!duplicated(triangle[ranked])]]] <- TRUE
  }
  nodes <- which(accepted)
  tin <- geometry::delaunayn(xy[nodes, , drop = FALSE])
  terrain <- rep(NA_real_, width * height)
  for (start in seq(1, height, by = 64)) {
    rows <- seq.int(start, min(height, start + 63)) - 1L
    indices <- as.vector(t(outer(rows * width, seq_len(width), '+')))
    query <- cbind(rep((seq_len(width) - .5) * args$dem_resolution, length(rows)),
      rep(-(rows + .5) * args$dem_resolution, each = width))
    lookup <- geometry::tsearch(xy[nodes, 1], xy[nodes, 2], tin, query[, 1], query[, 2], bary = TRUE)
    valid <- which(!is.na(lookup$idx))
    values <- rep(NA_real_, nrow(query))
    if (length(valid)) {
      vertices <- matrix(nodes[tin[lookup$idx[valid], , drop = FALSE]], ncol = 3)
      values[valid] <- rowSums(lookup$p[valid, , drop = FALSE] * matrix(z[vertices], ncol = 3)) / args$scale + header[['Min Z']]
    }
    distance <- RANN::nn2(xy[nodes, , drop = FALSE], query, k = 1)$nn.dists[, 1]
    values[distance > max(2 * args$dem_resolution, args$ptd_seed_size / 2)] <- NA_real_
    terrain[indices] <- values
  }
  if (!any(is.finite(terrain))) stop('PTD did not produce valid terrain cells.')
  raster <- terra::rast(nrows = height, ncols = width, xmin = header[['Min X']],
    xmax = header[['Min X']] + width * cell, ymax = header[['Max Y']],
    ymin = header[['Max Y']] - height * cell, crs = las_crs(header))
  terra::values(raster) <- terrain
  terra::writeRaster(raster, destination, datatype = 'FLT4S', overwrite = FALSE)
  message(sprintf('PTD terrain: %s ground nodes, %s DEM cells.', sum(accepted), sum(is.finite(terrain))))
}

las_crs <- function(header) {
  wkt <- rlas::header_get_wktcs(header)
  if (length(wkt) && nzchar(wkt)) return(wkt)
  epsg <- rlas::header_get_epsg(header)
  if (length(epsg) && epsg > 0) return(paste0('EPSG:', epsg))
  ''
}

sample_dem <- function(raster, values, x, y) {
  cells <- terra::cellFromXY(raster, cbind(x, y))
  values[cells]
}

dem_mesh <- function(raster, values, origin, scale, stride) {
  rows <- unique(c(seq.int(1L, nrow(raster), by = stride), nrow(raster)))
  cols <- unique(c(seq.int(1L, ncol(raster), by = stride), ncol(raster)))
  cells <- as.vector(t(outer((rows - 1L) * ncol(raster), cols, '+')))
  xy <- terra::xyFromCell(raster, cells)
  z <- values[cells]
  vertices <- cbind(xy[, 1] - origin[1], z - origin[3], xy[, 2] - origin[2]) * scale
  ids <- matrix(seq_along(cells), nrow = length(rows), byrow = TRUE)
  if (length(rows) < 2 || length(cols) < 2) stop('DEM needs at least two rows and columns.')
  p <- as.vector(ids[-nrow(ids), -ncol(ids), drop = FALSE])
  q <- as.vector(ids[-nrow(ids), -1, drop = FALSE])
  r <- as.vector(ids[-1, -ncol(ids), drop = FALSE])
  s <- as.vector(ids[-1, -1, drop = FALSE])
  triangles <- rbind(cbind(p, q, r), cbind(q, s, r))
  valid <- is.finite(z)
  triangles <- triangles[rowSums(matrix(valid[triangles], ncol = 3)) == 3, , drop = FALSE]
  if (!nrow(triangles)) stop('DEM has no valid surface triangles.')
  t <- vertices[triangles[1, ], , drop = FALSE]
  cross_y <- (t[2, 3] - t[1, 3]) * (t[3, 1] - t[1, 1]) - (t[2, 1] - t[1, 1]) * (t[3, 3] - t[1, 3])
  if (cross_y < 0) triangles <- triangles[, c(1, 3, 2), drop = FALSE]
  remap <- cumsum(valid)
  list(vertices = vertices[valid, , drop = FALSE], triangles = matrix(remap[triangles], ncol = 3))
}
