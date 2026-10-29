# Static core library. Later tasks append sources here (or the glob picks them up).
file(GLOB DM_CORE_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/src/*.c")
add_library(DiskMoveCore STATIC ${DM_CORE_SOURCES})
target_include_directories(DiskMoveCore PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")