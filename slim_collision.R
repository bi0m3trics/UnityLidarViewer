# Tight vertical obstacle proxies fitted from returns between shin and chest height.
collect_columns <- function(local, height, usable) {
  selected <- which(usable & is.finite(height) & height >= .30 & height < 1.70)
  cells <- data.table::data.table(x = floor(local[selected, 1] / .10), z = floor(local[selected, 3] / .10),
    band = floor((height[selected] - .30) / .35))
  cells[, list(count = .N), by = c('x', 'z', 'band')]
}

write_slim_scene <- function(out, columns, ground_at, origin, scale) {
  cells <- columns[, list(count = sum(count), mask = sum(unique(2 ^ band))), by = c('x', 'z')]
  key <- paste(cells$x, cells$z, sep = ',')
  masks <- new.env(hash = TRUE, parent = emptyenv())
  for (i in seq_len(nrow(cells))) masks[[key[i]]] <- as.integer(cells$mask[i])
  neighbors <- as.matrix(expand.grid(x = -1:1, z = -1:1))
  bits <- function(mask) sum(as.integer(intToBits(mask))[1:4])
  candidate <- logical(nrow(cells))
  for (i in which(cells$count >= 4)) {
    if (bits(cells$mask[i]) < 2) next
    combined <- as.integer(cells$mask[i])
    for (j in 1:9) {
      other <- masks[[paste(cells$x[i] + neighbors[j, 1], cells$z[i] + neighbors[j, 2], sep = ',')]]
      if (!is.null(other)) combined <- bitwOr(combined, other)
    }
    candidate[i] <- bitwAnd(combined, 9L) == 9L && bits(combined) >= 3
  }
  candidates <- new.env(hash = TRUE, parent = emptyenv())
  for (i in which(candidate)) candidates[[key[i]]] <- i
  colliders <- list(); rejected <- 0L
  for (seed in which(candidate)) {
    if (is.null(candidates[[key[seed]]])) next
    group <- integer(); queue <- seed; rm(list = key[seed], envir = candidates)
    while (length(queue)) {
      i <- queue[length(queue)]; queue <- queue[-length(queue)]; group <- c(group, i)
      for (j in 1:9) {
        neighbor <- paste(cells$x[i] + neighbors[j, 1], cells$z[i] + neighbors[j, 2], sep = ',')
        index <- candidates[[neighbor]]
        if (!is.null(index)) { queue <- c(queue, index); rm(list = neighbor, envir = candidates) }
      }
    }
    weights <- cells$count[group]
    if (sum(weights) < 24) next
    points <- (as.matrix(cells[group, c('x', 'z')]) + .5) * .10
    if (max(apply(points, 2, function(v) diff(range(v)))) > 1.6) { rejected <- rejected + 1L; next }
    centre <- colSums(points * weights) / sum(weights)
    distance <- sqrt(rowSums(sweep(points, 2, centre) ^ 2))
    radius <- max(.065, min(.55, as.numeric(quantile(distance, .85)) - .025))
    ground <- ground_at(centre[1] / scale + origin[1], centre[2] / scale + origin[2])
    if (!is.finite(ground)) next
    colliders[[length(colliders) + 1L]] <- list(x = centre[1], z = centre[2], radius = radius,
      points = sum(weights), y = (ground - origin[3]) * scale + .08, height = 1.60)
  }
  name <- 'slim-colliders.json'
  jsonlite::write_json(list(version = 1L, gridSize = .10, colliders = colliders,
    stats = list(candidateColumns = sum(candidate), rejectedBroadGroups = rejected)),
    file.path(out, name), pretty = TRUE, auto_unbox = TRUE, digits = NA)
  list(file = name, count = length(colliders))
}

collision_meshes <- function(counts, minimum, voxel, out) {
  occupied <- counts[count >= minimum]
  occupied[, tile := paste(floor(x / 16), floor(y / 16), floor(z / 16), sep = ',')]
  lookup <- new.env(hash = TRUE, parent = emptyenv())
  keys <- paste(occupied$x, occupied$y, occupied$z, sep = ',')
  for (key in keys) lookup[[key]] <- TRUE
  directions <- rbind(c(1,0,0), c(-1,0,0), c(0,1,0), c(0,-1,0), c(0,0,1), c(0,0,-1))
  corners <- list(rbind(c(1,0,0),c(1,1,0),c(1,1,1),c(1,0,1)), rbind(c(0,0,1),c(0,1,1),c(0,1,0),c(0,0,0)),
    rbind(c(0,1,1),c(1,1,1),c(1,1,0),c(0,1,0)), rbind(c(0,0,0),c(1,0,0),c(1,0,1),c(0,0,1)),
    rbind(c(1,0,1),c(1,1,1),c(0,1,1),c(0,0,1)), rbind(c(0,0,0),c(0,1,0),c(1,1,0),c(1,0,0)))
  files <- character()
  for (tile_key in unique(occupied$tile)) {
    cells <- as.matrix(occupied[occupied$tile == tile_key, c('x', 'y', 'z')])
    faces <- list()
    for (i in seq_len(nrow(cells))) for (face in 1:6) {
      neighbor <- paste(cells[i, ] + directions[face, ], collapse = ',')
      if (!is.null(lookup[[neighbor]])) next
      faces[[length(faces) + 1L]] <- sweep(corners[[face]], 2, cells[i, ], '+') * voxel
    }
    if (!length(faces)) next
    vertices <- do.call(rbind, faces)
    base <- seq(0, nrow(vertices) - 4, by = 4)
    triangles <- rbind(cbind(base + 1L, base + 2L, base + 3L), cbind(base + 1L, base + 3L, base + 4L))
    name <- sprintf('collision_%05d.bytes', length(files))
    write_mesh(file.path(out, name), vertices, triangles)
    files <- c(files, name)
  }
  list(files = files, count = nrow(occupied))
}
