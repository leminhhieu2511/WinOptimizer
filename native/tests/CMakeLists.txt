# One executable + one CTest test per test_*.c (except test_main.c).
file(GLOB DM_TEST_FILES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/test_*.c")
list(REMOVE_ITEM DM_TEST_FILES "${CMAKE_CURRENT_SOURCE_DIR}/test_main.c")

foreach(test_file ${DM_TEST_FILES})
  get_filename_component(test_base "${test_file}" NAME_WE)   # test_hash
  string(REGEX REPLACE "^test_" "" suite "${test_base}")     # hash
  add_executable(${test_base} "${test_file}" test_main.c mem_io.c)
  target_compile_definitions(${test_base} PRIVATE DM_SUITE=${suite})
  target_include_directories(${test_base} PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_link_libraries(${test_base} PRIVATE DiskMoveCore)
  add_test(NAME ${suite} COMMAND ${test_base})
endforeach()

# The Win32 layer test links DiskMove.dll's objects directly (internals included; temp files only).
target_compile_definitions(test_win32_io PRIVATE DMW_STATIC UNICODE _UNICODE)
target_include_directories(test_win32_io PRIVATE "${CMAKE_SOURCE_DIR}/DiskMove/src")
target_link_libraries(test_win32_io PRIVATE DiskMoveWin32)
