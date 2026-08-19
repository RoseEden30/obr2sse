add_library(nifwrap SHARED nifwrap.cpp)

target_link_libraries(nifwrap PRIVATE nifly)
target_include_directories(nifwrap PRIVATE ${NIFLY_INCLUDE_DIR} ${NIFLY_EXTERNAL_DIR})

set_target_properties(nifwrap PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
)